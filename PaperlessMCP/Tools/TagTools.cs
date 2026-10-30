using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PaperlessMCP.Client;
using PaperlessMCP.Models.Common;
using PaperlessMCP.Models.Tags;
using PaperlessMCP.Utils;
using static PaperlessMCP.Utils.ParsingHelpers;

namespace PaperlessMCP.Tools;

/// <summary>
/// MCP tools for tag operations.
/// </summary>
[McpServerToolType]
public static class TagTools
{
    [McpServerTool(Name = "paperless_tags_list")]
    [Description("List all tags with pagination.")]
    public static async Task<string> List(
        PaperlessClient client,
        [Description("Page number (default: 1)")] int page = 1,
        [Description("Page size (default: 25, capped by MAX_PAGE_SIZE)")] int pageSize = 25,
        [Description("Ordering field (e.g., 'name', '-document_count')")] string? ordering = null)
    {
        var effectivePageSize = client.GetEffectivePageSize(pageSize);
        var result = await client.GetTagsAsync(page, effectivePageSize, ordering).ConfigureAwait(false);

        var response = McpResponse<object>.Success(
            result.Results,
            new McpMeta
            {
                Page = page,
                PageSize = effectivePageSize,
                Total = result.Count,
                Next = result.Next,
                PaperlessBaseUrl = client.BaseUrl
            }
        );
        return JsonSerializer.Serialize(response);
    }

    [McpServerTool(Name = "paperless_tags_get")]
    [Description("Get a tag by its ID.")]
    public static async Task<string> Get(
        PaperlessClient client,
        [Description("Tag ID")] int id)
    {
        var tag = await client.GetTagAsync(id).ConfigureAwait(false);

        if (tag == null)
        {
            var errorResponse = McpErrorResponse.Create(
                ErrorCodes.NotFound,
                $"Tag with ID {id} not found",
                meta: new McpMeta { PaperlessBaseUrl = client.BaseUrl }
            );
            return JsonSerializer.Serialize(errorResponse);
        }

        var response = McpResponse<Tag>.Success(
            tag,
            new McpMeta { PaperlessBaseUrl = client.BaseUrl }
        );
        return JsonSerializer.Serialize(response);
    }

    [McpServerTool(Name = "paperless_tags_create")]
    [Description("Create a new tag.")]
    public static async Task<string> Create(
        PaperlessClient client,
        [Description("Tag name")] string name,
        [Description("Hex color (e.g., '#ff0000')")] string? color = null,
        [Description("Match pattern for auto-tagging")] string? match = null,
        [Description("Matching algorithm (0=None, 1=Any, 2=All, 3=Literal, 4=Regex, 5=Fuzzy, 6=Auto)")] int? matchingAlgorithm = null,
        [Description("Is inbox tag")] bool? isInboxTag = null,
        [Description("Parent tag ID for hierarchical tags (optional)")] int? parent = null)
    {
        var request = new TagCreateRequest
        {
            Name = name,
            Color = color,
            Match = match,
            MatchingAlgorithm = matchingAlgorithm,
            IsInboxTag = isInboxTag,
            Parent = parent
        };

        var result = await client.CreateTagWithResultAsync(request).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            // Same conflict-aware shape as the other attribute-create tools: a
            // unique-name rejection becomes CONFLICT with the existing tag's ID.
            var errorResponse = await ConflictDetection.BuildCreateFailureAsync(
                client, "tag", name, result.Error!).ConfigureAwait(false);
            return JsonSerializer.Serialize(errorResponse);
        }

        var response = McpResponse<Tag>.Success(
            result.Value!,
            new McpMeta { PaperlessBaseUrl = client.BaseUrl }
        );
        return JsonSerializer.Serialize(response);
    }

    [McpServerTool(Name = "paperless_tags_update")]
    [Description("Update an existing tag.")]
    public static async Task<string> Update(
        PaperlessClient client,
        [Description("Tag ID")] int id,
        [Description("New name (optional)")] string? name = null,
        [Description("Hex color (e.g., '#ff0000', optional)")] string? color = null,
        [Description("Match pattern (optional)")] string? match = null,
        [Description("Matching algorithm (optional)")] int? matchingAlgorithm = null,
        [Description("Is inbox tag (optional)")] bool? isInboxTag = null,
        [Description("Parent tag ID. Omit to leave the parent unchanged.")] int? parent = null,
        [Description("Set true to move the tag to the root level. Cannot be combined with parent.")] bool clearParent = false)
    {
        if (parent.HasValue && clearParent)
        {
            var errorResponse = McpErrorResponse.Create(
                ErrorCodes.Validation,
                "parent and clear_parent cannot be used together",
                meta: new McpMeta { PaperlessBaseUrl = client.BaseUrl }
            );
            return JsonSerializer.Serialize(errorResponse);
        }

        var request = new TagUpdateRequest
        {
            Name = name,
            Color = color,
            Match = match,
            MatchingAlgorithm = matchingAlgorithm,
            IsInboxTag = isInboxTag,
            Parent = clearParent
                ? TagParentUpdate.Clear
                : parent.HasValue
                    ? TagParentUpdate.Set(parent.Value)
                    : default
        };

        var tag = await client.UpdateTagAsync(id, request).ConfigureAwait(false);

        if (tag == null)
        {
            var errorResponse = McpErrorResponse.Create(
                ErrorCodes.NotFound,
                $"Tag with ID {id} not found or update failed",
                meta: new McpMeta { PaperlessBaseUrl = client.BaseUrl }
            );
            return JsonSerializer.Serialize(errorResponse);
        }

        var response = McpResponse<Tag>.Success(
            tag,
            new McpMeta { PaperlessBaseUrl = client.BaseUrl }
        );
        return JsonSerializer.Serialize(response);
    }

    [McpServerTool(Name = "paperless_tags_delete")]
    [Description("Delete a tag. Requires explicit confirmation.")]
    public static async Task<string> Delete(
        PaperlessClient client,
        [Description("Tag ID")] int id,
        [Description("Must be true to confirm deletion")] bool confirm = false)
    {
        if (!confirm)
        {
            var tag = await client.GetTagAsync(id).ConfigureAwait(false);

            if (tag == null)
            {
                var notFoundResponse = McpErrorResponse.Create(
                    ErrorCodes.NotFound,
                    $"Tag with ID {id} not found",
                    meta: new McpMeta { PaperlessBaseUrl = client.BaseUrl }
                );
                return JsonSerializer.Serialize(notFoundResponse);
            }

            var dryRunResponse = McpErrorResponse.Create(
                ErrorCodes.ConfirmationRequired,
                "Deletion requires confirm=true. This is a dry run showing what would be deleted.",
                new { tag_id = id, name = tag.Name, document_count = tag.DocumentCount },
                new McpMeta { PaperlessBaseUrl = client.BaseUrl }
            );
            return JsonSerializer.Serialize(dryRunResponse);
        }

        var success = await client.DeleteTagAsync(id).ConfigureAwait(false);

        if (!success)
        {
            var errorResponse = McpErrorResponse.Create(
                ErrorCodes.UpstreamError,
                $"Failed to delete tag with ID {id}",
                meta: new McpMeta { PaperlessBaseUrl = client.BaseUrl }
            );
            return JsonSerializer.Serialize(errorResponse);
        }

        var response = McpResponse<object>.Success(
            new { deleted = true, tag_id = id },
            new McpMeta { PaperlessBaseUrl = client.BaseUrl }
        );
        return JsonSerializer.Serialize(response);
    }

    [McpServerTool(Name = "paperless_tags_bulk_delete")]
    [Description("Delete multiple tags. Supports dry run mode.")]
    public static async Task<string> BulkDelete(
        PaperlessClient client,
        [Description("Tag IDs (comma-separated)")] string tagIds,
        [Description("Dry run mode - shows what would be deleted without applying")] bool dryRun = true,
        [Description("Must be true to execute the deletion")] bool confirm = false)
    {
        var ids = ParseIntArray(tagIds);

        if (ids == null || ids.Length == 0)
        {
            var errorResponse = McpErrorResponse.Create(
                ErrorCodes.Validation,
                "No valid tag IDs provided",
                meta: new McpMeta { PaperlessBaseUrl = client.BaseUrl }
            );
            return JsonSerializer.Serialize(errorResponse);
        }

        if (dryRun || !confirm)
        {
            var dryRunResult = new BulkOperationResult
            {
                AffectedIds = ids,
                Warnings = new List<string>
                {
                    dryRun ? "This is a dry run. Set dry_run=false and confirm=true to execute." : "Set confirm=true to execute the operation."
                },
                Executed = false
            };

            var dryRunResponse = McpResponse<BulkOperationResult>.Success(
                dryRunResult,
                new McpMeta { PaperlessBaseUrl = client.BaseUrl }
            );
            return JsonSerializer.Serialize(dryRunResponse);
        }

        var (success, bulkError) = await client.BulkEditObjectsAsync(ids, "tags", "delete").ConfigureAwait(false);

        if (!success)
        {
            var errorResponse = McpErrorResponse.Create(
                ErrorCodes.UpstreamError,
                $"Bulk delete operation failed: {bulkError}",
                meta: new McpMeta { PaperlessBaseUrl = client.BaseUrl }
            );
            return JsonSerializer.Serialize(errorResponse);
        }

        var result = new BulkOperationResult
        {
            AffectedIds = ids,
            Executed = true
        };

        var response = McpResponse<BulkOperationResult>.Success(
            result,
            new McpMeta { PaperlessBaseUrl = client.BaseUrl }
        );
        return JsonSerializer.Serialize(response);
    }

}
