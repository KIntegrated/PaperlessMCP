using System.Net;
using System.Text.Json;
using PaperlessMCP.Client;
using PaperlessMCP.Models.Common;

namespace PaperlessMCP.Utils;

/// <summary>
/// Turns raw upstream failures from attribute-create calls into structured MCP
/// errors. The critical case is Paperless's DRF unique-constraint rejection —
/// HTTP 400 with {"name": ["correspondent with this name already exists."]} on
/// the wire — which used to surface as an opaque UPSTREAM_ERROR with
/// details:null. A model that receives the ID of the existing object can
/// immediately switch to referencing/updating it instead of retrying blindly.
/// </summary>
public static class ConflictDetection
{
    /// <summary>Upper bound on pages scanned when resolving an existing object by name.</summary>
    public const int MaxLookupPages = 25;

    /// <summary>
    /// Decides whether an upstream API error is a unique-constraint violation and
    /// extracts the offending field and Paperless's own message.
    /// Paperless returns HTTP 400 (sometimes 409) with field-keyed DRF
    /// UniqueValidator messages containing "already exists".
    /// </summary>
    public static bool IsUniqueConstraintError(ApiError error, out string? field, out string? upstreamMessage)
    {
        field = null;
        upstreamMessage = null;

        if (error.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Conflict))
        {
            return false;
        }

        var body = error.ResponseBody;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (TryFindAlreadyExists(doc.RootElement, null, out field, out upstreamMessage))
            {
                return true;
            }
        }
        catch (JsonException)
        {
            // Non-JSON body — fall through to the string heuristic.
        }

        if (body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            field = "name";
            upstreamMessage = body;
            return true;
        }

        return false;
    }

    private static void Scan(JsonElement element, string? fieldPath, ref bool found, ref string? field, ref string? message)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    Scan(prop.Value, prop.Name, ref found, ref field, ref message);
                    if (found) return;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Scan(item, fieldPath, ref found, ref field, ref message);
                    if (found) return;
                }
                break;
            case JsonValueKind.String:
                var text = element.GetString();
                if (text?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                {
                    found = true;
                    field = fieldPath;
                    message = text;
                }
                break;
        }
    }

    /// <summary>Finds a DRF field error message containing "already exists".</summary>
    private static bool TryFindAlreadyExists(JsonElement element, string? fieldPath, out string? field, out string? message)
    {
        bool found = false;
        field = null;
        message = null;
        Scan(element, fieldPath, ref found, ref field, ref message);
        return found;
    }

    /// <summary>
    /// Builds the error envelope failures of paperless_*_create tools return when
    /// Paperless rejects the create. Unique conflicts become CONFLICT with the ID
    /// and name of the existing object resolved by a server-side lookup; other
    /// failures keep an actionable status and the upstream body in details.
    /// </summary>
    public static async Task<McpErrorResponse> BuildCreateFailureAsync(
        PaperlessClient client,
        string entityType,
        string name,
        ApiError error,
        CancellationToken cancellationToken = default)
    {
        if (IsUniqueConstraintError(error, out _, out var upstreamMessage))
        {
            var (existingId, existingName) = await FindExistingByNameAsync(client, entityType, name, cancellationToken)
                .ConfigureAwait(false);

            string message;
            if (existingId is int existing)
            {
                var caseNote = existingName != null && !string.Equals(existingName, name, StringComparison.Ordinal)
                    ? $" Existing entry is spelled '{existingName}'."
                    : string.Empty;
                message = $"A {entityType} named '{name}' already exists: ID {existing}.{caseNote} Reuse that ID instead of creating a duplicate.";
            }
            else
            {
                message = $"A {entityType} named '{name}' already exists (Paperless rejected the create), but the existing ID could not be resolved. Use paperless_attributes_search to look it up before retrying.";
            }

            return McpErrorResponse.Create(
                ErrorCodes.Conflict,
                message,
                new
                {
                    conflict = "unique_name",
                    entity = entityType,
                    name,
                    existing_id = existingId,
                    existing_name = existingName,
                    status_code = (int)error.StatusCode,
                    upstream_message = upstreamMessage
                },
                new McpMeta { PaperlessBaseUrl = client.BaseUrl });
        }

        return McpErrorResponse.Create(
            ErrorCodes.UpstreamError,
            $"Failed to create {entityType}: HTTP {(int)error.StatusCode} {error.Message}",
            new
            {
                status_code = (int)error.StatusCode,
                response_body = error.ResponseBody,
                upstream_message = error.Message
            },
            new McpMeta { PaperlessBaseUrl = client.BaseUrl });
    }

    /// <summary>
    /// Looks up an existing {entityType} by exact, case-insensitive name using a
    /// server-side name__icontains prefilter, so uniqueness conflicts can be
    /// answered with the existing object's ID instead of forcing the model to
    /// re-query. Returns (null, null) when no entry matches or the type is not
    /// recognized.
    /// </summary>
    public static async Task<(int? ExistingId, string? ExistingName)> FindExistingByNameAsync(
        PaperlessClient client,
        string entityType,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, null);
        }

        var pageSize = client.GetEffectivePageSize(null);

        for (var page = 1; page <= MaxLookupPages; page++)
        {
            List<(int Id, string Name)> items;
            switch (entityType.ToLowerInvariant())
            {
                case "tag":
                case "tags":
                {
                    var result = await client.GetTagsAsync(page, pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
                    items = result.Results.Select(t => (t.Id, t.Name)).ToList();
                    break;
                }
                case "correspondent":
                case "correspondents":
                {
                    var result = await client.GetCorrespondentsAsync(page, pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
                    items = result.Results.Select(c => (c.Id, c.Name)).ToList();
                    break;
                }
                case "document type":
                case "document_types":
                case "documenttypes":
                {
                    var result = await client.GetDocumentTypesAsync(page, pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
                    items = result.Results.Select(d => (d.Id, d.Name)).ToList();
                    break;
                }
                case "storage path":
                case "storage_paths":
                case "storagepaths":
                {
                    var result = await client.GetStoragePathsAsync(page, pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
                    items = result.Results.Select(s => (s.Id, s.Name)).ToList();
                    break;
                }
                default:
                    return (null, null);
            }

            // Default tuple has Name == null, so the null check doubles as the
            // "found" test. Comparison mirrors Paperless's UniqueValidator
            // (exact name equality, treated case-insensitively here so a
            // differently-cased duplicate is still reported).
            var (foundId, foundName) = items
                .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (foundName != null)
            {
                return (foundId, foundName);
            }

            if (items.Count < pageSize)
            {
                break;
            }
        }

        return (null, null);
    }
}
