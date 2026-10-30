using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PaperlessMCP.Client;
using PaperlessMCP.Models.Common;
using PaperlessMCP.Utils;
using System.Text.RegularExpressions;

namespace PaperlessMCP.Tools;

/// <summary>
/// MCP tool for wildcard/regex search over attribute names — tags, correspondents,
/// document types, storage paths and custom fields. Instances with hundreds of
/// correspondents make full listing unusable for the model (context cost, plus
/// ASCII-only ordering that sorts case-insensitively-named entries to the very
/// end), so this searches server-side with a contains-prefilter and returns only
/// compact id/name/type records.
/// </summary>
[McpServerToolType]
public static class AttributeSearchTools
{
    /// <summary>Upper bound on pages fetched per entity type (25 × MAX_PAGE_SIZE items).</summary>
    /// <remarks>Mutable for tests that must exercise the page-cap path.</remarks>
    public static int MaxPagesPerEntity = 25;

    /// <summary>Upper bound on matched results returned per entity type.</summary>
    public const int MaxMatchesPerEntity = 100;

    /// <summary>The entity types this tool can search, in result-stable order.</summary>
    public static readonly string[] KnownEntities =
    [
        "tags",
        "correspondents",
        "document_types",
        "storage_paths",
        "custom_fields"
    ];

    [McpServerTool(Name = "paperless_attributes_search")]
    [Description("Find existing tags, correspondents, document types, storage paths or custom fields by name without paging through long lists. Pattern is a wildcard by default: '*' matches any run of characters, '?' exactly one character (e.g. 'acme*', '*insurance*'). Set regex=true to use a raw regular expression instead. Matching is case-insensitive; set match_case=true for exact-cased matching. Returns the IDs of matches so they can be used directly in other tools.")]
    public static async Task<string> Search(
        PaperlessClient client,
        [Description("Wildcard or regex pattern matched against attribute names")] string pattern,
        [Description("Entity types to search, comma-separated: tags, correspondents, document_types, storage_paths, custom_fields (default: all of them)")] string? entities = null,
        [Description("Treat the pattern as a raw regular expression instead of a wildcard")] bool regex = false,
        [Description("Match case-sensitively (default: case-insensitive)")] bool matchCase = false,
        [Description("Also match against the auto-assignment 'match' pattern where the entity type has one")] bool includeMatchPatterns = false)
    {
        if (!WildcardPattern.TryCompile(pattern, regex, !matchCase, out var compiled, out var compileError))
        {
            var compileFailure = McpErrorResponse.Create(
                ErrorCodes.Validation,
                compileError!,
                new { pattern, mode = regex ? "regex" : "wildcard" },
                new McpMeta { PaperlessBaseUrl = client.BaseUrl });
            return JsonSerializer.Serialize(compileFailure);
        }

        var wanted = ParseEntities(entities);
        if (wanted.Count == 0)
        {
            var invalidEntities = McpErrorResponse.Create(
                ErrorCodes.Validation,
                $"No valid entity types in '{entities}'. Valid types: {string.Join(", ", KnownEntities)}",
                new { requested = entities, valid = KnownEntities },
                new McpMeta { PaperlessBaseUrl = client.BaseUrl });
            return JsonSerializer.Serialize(invalidEntities);
        }

        // Wildcard patterns with a literal middle chunk can narrow the API query
        // server-side (name__icontains). Regex patterns show up escaped in the
        // match but the paperless server would see raw regex characters, so only
        // wildcard mode may use the prefilter.
        var prefilter = regex ? null : WildcardPattern.GetLiteralPrefilter(pattern);

        var warnings = new List<string>();
        var matches = new List<object>();
        var matchedCounts = new Dictionary<string, int>();
        var pageSize = client.GetEffectivePageSize(null);

        foreach (var entity in wanted)
        {
            var entityMatches = new List<object>();
            var entityTruncated = false;
            var pagesLoaded = 0;
            var lastPageWasFull = false;

            for (var page = 1; page <= MaxPagesPerEntity && !entityTruncated; page++)
            {
                var items = await FetchEntityPageAsync(client, entity, page, pageSize, prefilter)
                    .ConfigureAwait(false);

                if (items.Count == 0)
                {
                    break;
                }

                pagesLoaded = page;
                lastPageWasFull = items.Count >= pageSize;

                foreach (var (id, name, matchPattern) in items)
                {
                    var nameMatched = TryMatch(compiled!, name, out var nameTimedOut);
                    var matchTimedOut = false;
                    var matchMatched = includeMatchPatterns
                        && matchPattern != null
                        && TryMatch(compiled!, matchPattern, out matchTimedOut);
                    if (nameTimedOut || matchTimedOut)
                    {
                        var timeoutError = McpErrorResponse.Create(
                            ErrorCodes.Validation,
                            $"Pattern exceeded the {WildcardPattern.MatchTimeout.TotalSeconds:F0}s match limit while scanning {entity}. Make it more specific.",
                            new { pattern, mode = regex ? "regex" : "wildcard" },
                            new McpMeta { PaperlessBaseUrl = client.BaseUrl });
                        return JsonSerializer.Serialize(timeoutError);
                    }

                    if (!nameMatched && !matchMatched)
                    {
                        continue;
                    }

                    if (entityMatches.Count >= MaxMatchesPerEntity)
                    {
                        entityTruncated = true;
                        break;
                    }

                    entityMatches.Add(new { id, name, type = entity });
                }

                if (items.Count < pageSize)
                {
                    // The last, partially filled page — beyond the API's total.
                    break;
                }
            }

            matchedCounts[entity] = entityMatches.Count;
            matches.AddRange(entityMatches);

            if (entityTruncated)
            {
                warnings.Add($"{entity}: more than {MaxMatchesPerEntity} matches; output truncated. Refine the pattern or search fewer entity types.");
            }
            else if (pagesLoaded == MaxPagesPerEntity && lastPageWasFull)
            {
                // Page budget exhausted with data still flowing: with a small
                // MAX_PAGE_SIZE this can mean only a few thousand items scanned —
                // say so instead of letting the model assume completeness.
                warnings.Add($"{entity}: search stopped after fetching {MaxPagesPerEntity} pages (page size {pageSize}); results may be incomplete. Narrow the pattern, narrow the entity types, or raise MAX_PAGE_SIZE.");
            }
        }

        var response = McpResponse<object>.Success(
            new
            {
                pattern,
                mode = regex ? "regex" : "wildcard",
                entities = wanted,
                matches,
                counts = matchedCounts
            },
            new McpMeta { PaperlessBaseUrl = client.BaseUrl },
            warnings);
        return JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// RegexMatchTimeoutException aborts a tool call otherwise; a pathological
    /// pattern must surface as an actionable error instead.
    /// </summary>
    private static bool TryMatch(Regex compiled, string input, out bool timedOut)
    {
        timedOut = false;
        try
        {
            return compiled.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
            return false;
        }
    }

    private static List<string> ParseEntities(string? entities)
    {
        if (string.IsNullOrWhiteSpace(entities))
        {
            return [.. KnownEntities];
        }

        var requested = entities
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .Where(e => KnownEntities.Contains(e))
            .Distinct()
            .ToList();

        // Keep the stable KnownEntities order for deterministic output.
        return KnownEntities.Where(requested.Contains).ToList();
    }

    private static async Task<List<(int Id, string Name, string? Match)>> FetchEntityPageAsync(
        PaperlessClient client,
        string entity,
        int page,
        int pageSize,
        string? prefilter)
    {
        switch (entity)
        {
            case "tags":
            {
                var result = await client.GetTagsAsync(page, pageSize, nameFilter: prefilter).ConfigureAwait(false);
                return result.Results.Select(t => (t.Id, t.Name, t.Match)).ToList();
            }
            case "correspondents":
            {
                var result = await client.GetCorrespondentsAsync(page, pageSize, nameFilter: prefilter).ConfigureAwait(false);
                return result.Results.Select(c => (c.Id, c.Name, c.Match)).ToList();
            }
            case "document_types":
            {
                var result = await client.GetDocumentTypesAsync(page, pageSize, nameFilter: prefilter).ConfigureAwait(false);
                return result.Results.Select(d => (d.Id, d.Name, d.Match)).ToList();
            }
            case "storage_paths":
            {
                var result = await client.GetStoragePathsAsync(page, pageSize, nameFilter: prefilter).ConfigureAwait(false);
                return result.Results.Select(s => (s.Id, s.Name, s.Match)).ToList();
            }
            case "custom_fields":
            {
                var result = await client.GetCustomFieldsAsync(page, pageSize, nameFilter: prefilter).ConfigureAwait(false);
                return result.Results.Select(f => (f.Id, f.Name, (string?)null)).ToList();
            }
            default:
                return [];
        }
    }
}
