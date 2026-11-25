using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using VirtoCommerce.SiteBuilder.MCP.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class HistoryTools(
    IAuthorizationService authorizationService,
    IContentHistoryOperationService contentHistoryOperationService,
    ISiteBuilderContentQueryService contentQueryService,
    ILogger<HistoryTools> logger)
{
    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
        Justification = "Each parameter is an argument of the MCP tool's input schema.")]
    [McpServerTool(Name = "list_versions", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Description("Saved versions of a database page or of theme files of a store, newest first: version id, date, " +
        "author, what the save said, whether the version was published, and the items it changed. Says when the " +
        "store keeps no history. Returns one page; pass next_cursor as cursor to read older versions. For a page stored " +
        "as a file (page_id is its path, 'pages/about.page'), the commits of the page in the store's repository: the " +
        "unpublished ones on draft branches first (mine marks your own draft), then the published ones; a store that keeps " +
        "such pages in blob storage has none. version_id of any row is what restore_version takes.")]
    public async Task<ContentVersionsAnswer> ListVersions(
        RequestContext<CallToolRequestParams> context,
        IMcpPageSizeResolver pageSizeResolver,
        [Description("Store id.")] string store_id,
        [Description("Id of a database page, or the path of a page stored as a file ('pages/about.page'); search_site_pages returns both.")] string page_id = null,
        [Description("Theme files by their path with the theme folder, for example 'default/templates/product.json' or 'default/config/settings_data.json'.")] string[] theme_files = null,
        [Description("next_cursor of the previous answer, to read older versions.")] string cursor = null,
        [Description("Versions to return. Omit it for the default page size.")] int? take = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(store_id))
        {
            throw new McpException("Pass store_id: the history is kept per store.");
        }

        if (PageLookup.IsFilePagePath(page_id))
        {
            return await ListFilePageVersionsAsync(context.User, store_id, page_id, theme_files, cursor, await pageSizeResolver.GetPageSizeAsync(take),
                cancellationToken);
        }

        var items = GetItems(page_id, theme_files);

        // The store first, so an answer about the arguments does not tell which stores exist.
        await authorizationService.EnsureStoreAllowedAsync(context.User, store_id);

        if (items.Count == 0)
        {
            throw new McpException("Pass page_id, theme_files or both.");
        }

        var request = new ContentHistoryListOperationRequest
        {
            StoreId = store_id,
            Items = items,
            Cursor = cursor,
            Take = await pageSizeResolver.GetPageSizeAsync(take),
        };
        var result = await FilePages.RunAsync(logger, () => contentHistoryOperationService.ListAsync(request, context.User, cancellationToken));

        return result.Status switch
        {
            SiteBuilderOperationStatus.Succeeded => ContentVersionsAnswer.From(store_id, result.Value),
            SiteBuilderOperationStatus.NotFound => throw new McpException($"Page '{page_id}' was not found. Call search_site_pages to find its id."),
            SiteBuilderOperationStatus.Forbidden => throw new McpForbiddenException($"Page '{page_id}' is not a page of store '{store_id}' you can read."),
            _ when result.Error == ContentHistoryOperationService.UnknownCursorError =>
                throw new McpException("The cursor does not belong to this history. Call list_versions without cursor."),
            _ => throw new McpException(result.Error),
        };
    }

    private async Task<ContentVersionsAnswer> ListFilePageVersionsAsync(ClaimsPrincipal user, string storeId, string pageId, string[] themeFiles,
        string cursor, int take, CancellationToken cancellationToken)
    {
        var page = await FilePages.LoadAsync(authorizationService, user, storeId, pageId);
        if (themeFiles?.Any(x => !string.IsNullOrWhiteSpace(x)) == true)
        {
            throw new McpException("A page stored as a file has a history of its own, apart from the theme: call list_versions once for each.");
        }

        return await FilePageVersions.ListAsync(contentQueryService, logger, user, page, cursor, take, cancellationToken);
    }

    private static List<string> GetItems(string pageId, string[] themeFiles)
    {
        var items = new List<string>();
        if (!string.IsNullOrEmpty(pageId))
        {
            items.Add(new HistoryItem(HistoryItemType.Page, pageId).ToString());
        }

        items.AddRange((themeFiles ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => HistoryItem.ForThemeFile(x.Trim()).ToString()));

        return items;
    }
}

public sealed class ContentVersionsAnswer
{
    public string StoreId { get; init; }

    public string History { get; init; }

    public bool HistoryAvailable { get; init; }

    public string Note { get; init; }

    public IList<ContentVersionRow> Versions { get; init; }

    public string NextCursor { get; init; }

    public bool HasMore => NextCursor != null;

    public static ContentVersionsAnswer From(string storeId, ContentVersionPage page)
    {
        var (history, note) = Describe(storeId, page.Status);

        return new ContentVersionsAnswer
        {
            StoreId = storeId,
            History = history,
            HistoryAvailable = page.Status == ContentHistoryStatus.Enabled,
            Note = note,
            Versions = page.Versions.Select(ContentVersionRow.From).ToList(),
            NextCursor = page.Cursor,
        };
    }

    public static (string History, string Note) Describe(string storeId, ContentHistoryStatus status) => status switch
    {
        ContentHistoryStatus.Enabled => ("enabled", null),
        ContentHistoryStatus.SetupRequired => ("setup_required",
            $"Store '{storeId}' keeps no history yet: Site Builder's GitHub connection or the store's repository is not set up."),
        _ => ("unavailable",
            $"Store '{storeId}' keeps no history: its repository was assigned by hand, so database pages and theme files are not versioned."),
    };
}

public sealed class ContentVersionRow
{
    public string VersionId { get; init; }

    public DateTime? Date { get; init; }

    public string Author { get; init; }

    public string Description { get; init; }

    public bool Published { get; init; }

    public IList<string> Items { get; init; }

    public bool? Mine { get; init; }

    public bool? Bulk { get; init; }

    public static ContentVersionRow From(ContentVersion version)
    {
        return new ContentVersionRow
        {
            VersionId = version.VersionId,
            Date = version.Date,
            Author = version.Author?.Name ?? version.Author?.Login,
            Description = version.Description,
            Published = version.Published,
            Items = version.Items,
        };
    }

    public static ContentVersionRow From(PageVersion version, FilePage page) => new()
    {
        VersionId = version.Sha,
        Date = version.Date,
        Author = version.Author?.Name ?? version.Author?.Login ?? version.VcUser,
        Description = version.Message,
        Published = version.Published,
        Items = [page.VersionKey],
        Mine = version.Mine,
        Bulk = version.Bulk,
    };
}
