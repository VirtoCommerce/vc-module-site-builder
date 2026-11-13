using System.ComponentModel;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
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
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Saved versions of a page or a theme file, from the store's git history. A store keeps history only when its content
// is in a repository Site Builder set up; the answer says so when it does not. A page stored as a file has a history of
// its own, the commits of its file, when the store keeps such pages in git.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class HistoryTools(
    IAuthorizationService authorizationService,
    IContentHistory contentHistory,
    IValidator<ContentHistoryRequest> requestValidator,
    IGroupedPageService groupedPageService,
    IContentPermissionService contentPermissionService,
    ISiteBuilderContentQueryService contentQueryService)
{
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

        var request = new ContentHistoryRequest { StoreId = store_id, Cursor = cursor, Items = GetItems(page_id, theme_files) };

        // The store first, then the request, then the page, as the designer does, so an answer does not tell which
        // stores and pages exist.
        await authorizationService.EnsureAllowedAsync(context.User, request, store_id);

        if (request.Items.Count == 0)
        {
            throw new McpException("Pass page_id, theme_files or both.");
        }

        var validation = await requestValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            throw new McpException(string.Join(" ", validation.Errors.Select(x => x.ErrorMessage).Distinct()));
        }

        if (!string.IsNullOrEmpty(page_id))
        {
            await EnsurePageAsync(context.User, store_id, page_id);
        }

        var items = request.Items.Select(x => HistoryItem.TryParse(x, out var item) ? item : null).Where(x => x != null).ToList();

        ContentVersionPage page;
        try
        {
            page = await contentHistory.ListAsync(store_id, items, cursor, await pageSizeResolver.GetPageSizeAsync(take), cancellationToken);
        }
        catch (ArgumentException)
        {
            throw new McpException("The cursor does not belong to this history. Call list_versions without cursor.");
        }

        return ContentVersionsAnswer.From(store_id, page);
    }

    private async Task EnsurePageAsync(ClaimsPrincipal user, string storeId, string pageId)
    {
        var group = await groupedPageService.GetByIdAsync(pageId)
            ?? throw new McpException($"Page '{pageId}' was not found. Call search_site_pages to find its id.");

        if (!await contentPermissionService.CanAccessPageAsync(user, group, storeId))
        {
            throw new McpForbiddenException($"Page '{pageId}' is not a page of store '{storeId}' you can read.");
        }
    }

    private async Task<ContentVersionsAnswer> ListFilePageVersionsAsync(ClaimsPrincipal user, string storeId, string pageId, string[] themeFiles,
        string cursor, int take, CancellationToken cancellationToken)
    {
        var page = await FilePages.LoadAsync(authorizationService, user, storeId, pageId);
        if (themeFiles?.Any(x => !string.IsNullOrWhiteSpace(x)) == true)
        {
            throw new McpException("A page stored as a file has a history of its own, apart from the theme: call list_versions once for each.");
        }

        return await FilePageVersions.ListAsync(contentQueryService, user, page, cursor, take, cancellationToken);
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

    // enabled, setup_required or unavailable.
    public string History { get; init; }

    public bool HistoryAvailable { get; init; }

    // Why there is no history, when there is none.
    public string Note { get; init; }

    public IList<ContentVersionRow> Versions { get; init; }

    // Pass it as cursor for older versions; null on the last page.
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

    // The state of a store's history as the tools name it, with why there is none.
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

    // The version holds what is live for every item it changed.
    public bool Published { get; init; }

    // page:{id} and theme:{path} of the items the version changed.
    public IList<string> Items { get; init; }

    // A page stored as a file in git: the version is on your own draft branch.
    public bool? Mine { get; init; }

    // A page stored as a file in git: a commit that changed far more than this page, such as an import.
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

    // A commit of a page stored as a file: its id is what restore_version takes as version_id.
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
