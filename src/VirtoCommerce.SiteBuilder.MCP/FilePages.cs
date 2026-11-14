using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.MCP;

// A page or a blog article kept as a file, in git or in blob storage: named by its content folder and its path in it, as
// the designer opens it, so the name always holds a '/' and never looks like the id of a database page.
public sealed record FilePage(string StoreId, string ContentType, string Path)
{
    public string PageId => $"{ContentType}/{Path}";

    // The key of the page in expected_versions of restore_version.
    public string VersionKey => $"page:{PageId}";
}

public static class FilePages
{
    private const string Pages = "pages";
    private const string Blogs = "blogs";

    public const string PageIdDescription = "A page stored as a file is named by its folder and path, for example 'pages/about.page' or " +
        "'blogs/news/launch.page', and needs store_id.";

    public static FilePage Parse(string storeId, string pageId)
    {
        var id = pageId?.Trim() ?? string.Empty;
        var slash = id.IndexOf('/');
        var folder = slash < 0 ? string.Empty : id[..slash].ToLowerInvariant();
        var path = slash < 0 ? string.Empty : id[(slash + 1)..].Trim('/');
        if (folder is not (Pages or Blogs) || path.Length == 0)
        {
            throw new McpException($"'{pageId}' is not a page. {PageIdDescription} A database page is named by its id from search_site_pages.");
        }

        if (string.IsNullOrWhiteSpace(storeId))
        {
            throw new McpException($"'{pageId}' is a page stored as a file: pass store_id too, the store it belongs to.");
        }

        return new FilePage(storeId.Trim(), folder, path);
    }

    // The store first, as the operations check it, so a refusal names the store before anything about the page.
    public static async Task<FilePage> LoadAsync(IAuthorizationService authorizationService, ClaimsPrincipal user, string storeId, string pageId)
    {
        var page = Parse(storeId, pageId);
        await authorizationService.EnsureAllowedAsync(user, new SiteBuilderPageSearchCriteria { StoreId = page.StoreId }, page.StoreId);

        return page;
    }

    // The page as the designer edits it (the user's draft, else what is published), or what is published; null when
    // there is no such page.
    public static async Task<string> ReadAsync(ISiteBuilderContentQueryService contentQueryService, ILogger logger, ClaimsPrincipal user, FilePage page,
        bool draft, CancellationToken cancellationToken)
    {
        var result = await RunAsync(logger, () => contentQueryService.ReadFileAsync(new SiteBuilderFileReadRequest
        {
            StoreId = page.StoreId,
            ContentType = page.ContentType,
            Path = page.Path,
            Draft = draft,
        }, user, cancellationToken));

        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return await ReadTextAsync(result.Value);
            case SiteBuilderOperationStatus.NotFound:
                return null;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException(Forbidden(page, "read"));
            default:
                throw new McpException($"Page '{page.PageId}' could not be read: {result.Error}");
        }
    }

    public static async Task<string> RequireAsync(ISiteBuilderContentQueryService contentQueryService, ILogger logger, ClaimsPrincipal user, FilePage page,
        CancellationToken cancellationToken) =>
        await ReadAsync(contentQueryService, logger, user, page, draft: true, cancellationToken)
        ?? throw new McpException($"Page '{page.PageId}' was not found in store '{page.StoreId}'. Check the folder and path the designer shows for it.");

    // A file page keeps no version of its own: its token is the hash of the content as read, the one the operations
    // compare at their write, so an edit, a restore or a publish of content that changed since is refused.
    public static string VersionOf(string content) => SiteBuilderContentVersion.CreateForFileContent(content);

    public static string Forbidden(FilePage page, string verb) =>
        $"You may not {verb} page '{page.PageId}': it needs builder:read in store '{page.StoreId}'.";

    // Git failing is not an outcome of the operations but an exception, whose message is for an administrator.
    public static async Task<T> RunAsync<T>(ILogger logger, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (GitContentException ex)
        {
            throw new McpException($"{GitContentFailures.Describe(logger, ex)} Nothing more was done.");
        }
    }

    public static void EnsureSucceeded(SiteBuilderOperationResult result, FilePage page, string verb)
    {
        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException(Forbidden(page, verb));
            case SiteBuilderOperationStatus.NotFound:
                throw new McpException($"Page '{page.PageId}' was not found in store '{page.StoreId}', or the store keeps no versions of it.");
            default:
                var details = result.Errors?.Count > 0 ? $" {string.Join("; ", result.Errors)}" : string.Empty;
                throw new McpException($"Page '{page.PageId}' was not changed: {result.Error}{details}");
        }
    }

    private static async Task<string> ReadTextAsync(SiteBuilderFileContent file)
    {
        if (file?.Stream == null)
        {
            return file?.Text;
        }

        await using var stream = file.Stream;
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync();
    }
}

// Where a publish or an unpublish of a page stored as a file stands: in blob storage it is done at once; in git it is a
// pull request that merged, waits for checks, or conflicts.
public sealed class FilePageShipmentRow
{
    // In blob storage done; in git merged, pending, awaiting_merge, already_published or conflict.
    [JsonPropertyName("state")]
    public string State { get; init; }

    // git or blob.
    [JsonPropertyName("flow")]
    public string Flow { get; init; }

    [JsonPropertyName("pull_request")]
    public int? PullRequest { get; init; }

    [JsonPropertyName("pull_request_url")]
    public string PullRequestUrl { get; init; }

    // A conflicting publish has a way out: publishing again with rebase, which needs a new approval.
    [JsonPropertyName("can_rebase")]
    public bool CanRebase { get; init; }

    public static FilePageShipmentRow From(FilePageShipment shipment) => shipment == null ? null : new FilePageShipmentRow
    {
        State = !shipment.GitFlow ? "done" : shipment.State switch
        {
            GitPublishState.Merged => "merged",
            GitPublishState.Pending => "pending",
            GitPublishState.AwaitingMerge => "awaiting_merge",
            GitPublishState.AlreadyPublished => "already_published",
            _ => "conflict",
        },
        Flow = shipment.GitFlow ? "git" : "blob",
        PullRequest = shipment.Merge?.PullRequestNumber,
        PullRequestUrl = shipment.Merge?.Url,
        CanRebase = shipment.State == GitPublishState.Conflict && shipment.CanRebase,
    };
}
