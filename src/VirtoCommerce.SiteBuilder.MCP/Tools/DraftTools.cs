using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Dropping a draft loses its author's unpublished work, and customers see nothing of it either way: the user confirms
// it in the chat, and a client that cannot ask does not discard. What is discarded is named by kind, each with its own
// argument: a page by page_id (a database page by its id, a page stored as a file by its path and store_id), a file of
// the theme by theme_file. The question lists the arguments, which name it. The call takes the draft its dry run showed:
// it sends back the versions the dry run answers (expected_versions), and a draft a colleague saved since is not dropped.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class DraftTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageOperationService groupedPageOperationService,
    IDesignerOperationService designerOperationService,
    IThemeOperationService themeOperationService,
    ISiteBuilderContentQueryService contentQueryService,
    IContentPermissionService contentPermissionService,
    IFilePageOperationService filePageOperationService,
    ILogger<DraftTools> logger)
{
    public const string PageKind = "page";
    public const string ThemeFileKind = "theme_file";

    [McpServerTool(Name = "discard_draft", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresConfirmation(Fallback = McpConfirmationFallback.Refuse)]
    [Description("Discard the draft of a page or of a theme file: its unpublished changes are deleted and it is its " +
        "published version again. Something never published has nothing to go back to and is refused. A page is a " +
        "database page by its id, or a page stored as a file by its path ('pages/about.page') with store_id; in git, its " +
        "draft is your own draft branch of the page. A theme file is a " +
        "template, the template rules, the header, the footer, the brand or a palette, named by its path as search_templates " +
        "and list_versions name it; discarding it needs builder:templates for templates and rules, builder:theme for the others. " +
        "First call it with dryRun: it shows the changes the discard undoes and answers expected_versions. Then call it without " +
        "dryRun and with those expected_versions unchanged: a draft saved again in between is not discarded, and a call without " +
        "them is refused. The user confirms the call in the chat; a client that cannot ask refuses it, and the user discards the " +
        "draft in the designer instead. Returns the changes the discard undoes.")]
    public async Task<SiteBuilderChange> DiscardDraft(
        RequestContext<CallToolRequestParams> context,
        [Description("What to discard the draft of: 'page' or 'theme_file'.")] string kind,
        [Description("page: id of a Landing Page or of a language version (search_site_pages), or the path of a page stored as a file.")] string page_id = null,
        [Description("theme_file, or a page stored as a file: the store of the theme or of the page.")] string store_id = null,
        [Description("theme_file: the file with the theme folder, for example 'default/templates/product.bicycles.json' or 'default/config/palette.json'.")] string theme_file = null,
        [Description(ReviewedVersions.Description)] Dictionary<string, string> expected_versions = null,
        [Description("true to return what the discard would undo, without discarding.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (kind == ThemeFileKind)
        {
            return await DiscardThemeFileAsync(context, store_id, theme_file, expected_versions, dryRun, cancellationToken);
        }

        if (kind != PageKind)
        {
            throw new McpException($"kind must be '{PageKind}' or '{ThemeFileKind}'.");
        }

        if (PageLookup.IsFilePagePath(page_id))
        {
            return await DiscardFilePageAsync(context, store_id, page_id, expected_versions, dryRun, cancellationToken);
        }

        var group = await groupedPageService.LoadPageAsync(authorizationService, context.User, page_id);
        var change = new SiteBuilderChange
        {
            Action = "discard_draft",
            ObjectType = ChangePreview.Page,
            ObjectId = group.Id,
            ObjectName = group.Name,
            PageId = group.Id,
            MultilanguagePageId = group.MultilanguagePageId,
        };

        await SiteBuilderChanges.RunAsync(change,
            isDryRun => groupedPageOperationService.DiscardDraftAsync(new GroupedPageDiscardOperationRequest
            {
                GroupId = group.Id,
                SaveId = Guid.NewGuid().ToString("N"),
                ExpectedDraftVersion = isDryRun ? null : ReviewedVersions.Of(change, group.Id),
                DryRun = isDryRun,
                ReportImpact = true,
            }, context.User, cancellationToken),
            x => EnsureDiscarded(x, change, group),
            dryRun,
            checkAsync: x => PinAsync(change, x.Preview?.Objects.Select(item => Version(item.ObjectId, item.ETag)), expected_versions, dryRun));

        return change;
    }

    // The draft's changes over the live file, then, without dryRun, the designer's discard, which checks the part's
    // permission. The designer's discard keeps a draft without a live file silently; here it is refused, as a page's is.
    private async Task<SiteBuilderChange> DiscardThemeFileAsync(RequestContext<CallToolRequestParams> context, string storeId, string themeFile,
        IDictionary<string, string> expectedVersions, bool dryRun, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(themeFile))
        {
            throw new McpException("Pass store_id and theme_file, the file with the theme folder such as 'default/config/palette.json'.");
        }

        await authorizationService.EnsureAllowedAsync(context.User, new SiteBuilderPageSearchCriteria { StoreId = storeId }, storeId);

        var path = themeFile.Trim().TrimStart('/');
        var pathInTheme = ThemeFileEdits.GetPathInTheme(path);
        var permission = contentPermissionService.GetThemeFileEditPermission(path);
        var status = await themeOperationService.GetStatusAsync(new SiteBuilderOperationRequest { StoreId = storeId }, context.User, cancellationToken);
        SiteBuilderChanges.EnsureSucceeded(status, $"The theme of store '{storeId}'", $"Your builder:read permission does not cover store '{storeId}'.");

        if (!status.Value.Drafts.Contains(pathInTheme, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException($"'{path}' has no unpublished changes, so there is nothing to discard. The files with drafts: " +
                $"{(status.Value.Drafts.Count == 0 ? "none" : string.Join(", ", status.Value.Drafts))}.");
        }

        if (status.Value.NewDrafts.Contains(pathInTheme, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException($"'{path}' was never published, so its draft is all there is and is not discarded. Change it with the design tools instead.");
        }

        var draft = await ThemeFiles.ReadAsync(designerOperationService, context.User, storeId, path, cancellationToken);
        var live = await ThemeFiles.ReadLiveAsync(contentQueryService, context.User, storeId, path, cancellationToken);
        var change = new SiteBuilderChange
        {
            Action = "discard_draft",
            ObjectType = ChangePreview.ThemeFile,
            ObjectId = path,
            ObjectName = path,
            DryRun = true,
        };

        var changes = ThemeFileEdits.Compare(path, draft.Content, live);
        foreach (var item in changes)
        {
            change.AddChange(item.Field, item.From, item.To);
        }

        if (changes.Count == 0)
        {
            change.AddChange("draft", "saved draft", "published version");
        }

        // The version of the draft as the designer's discard compares it.
        var forbidden = $"You may not discard '{path}': it needs builder:update and {permission}, and builder:read in store '{storeId}'.";
        var check = await designerOperationService.DiscardAsync(ThemeFileDiscard(storeId, path, isDryRun: true, versions: null), context.User, cancellationToken);
        EnsureNotChanged(check, change);
        SiteBuilderChanges.EnsureSucceeded(check, $"'{path}'", forbidden);
        await PinAsync(change, check.Preview?.Objects.Where(x => x.ObjectType == ChangePreview.ThemeFile).Select(x => Version(x.ObjectId, x.ETag)),
            expectedVersions, dryRun);
        if (dryRun)
        {
            return change;
        }

        var result = await designerOperationService.DiscardAsync(ThemeFileDiscard(storeId, path, isDryRun: false, change.ExpectedVersions), context.User,
            cancellationToken);
        EnsureNotChanged(result, change);
        SiteBuilderChanges.EnsureSucceeded(result, $"'{path}'", forbidden);

        change.DryRun = false;
        return change;
    }

    private static DesignerDiscardOperationRequest ThemeFileDiscard(string storeId, string path, bool isDryRun, IDictionary<string, string> versions) => new()
    {
        StoreId = storeId,
        Changes = new DesignerDiscardRequest { ThemeFiles = [path] },
        SaveId = Guid.NewGuid().ToString("N"),
        ExpectedThemeFileVersions = versions == null ? null : new Dictionary<string, string>(versions),
        DryRun = isDryRun,
    };

    // In blob storage the draft is a file next to the page; a store that keeps its pages in git refuses that, and its draft is
    // the user's own draft branch of the page instead.
    private async Task<SiteBuilderChange> DiscardFilePageAsync(RequestContext<CallToolRequestParams> context, string storeId, string pageId,
        IDictionary<string, string> expectedVersions, bool dryRun, CancellationToken cancellationToken)
    {
        var page = await FilePages.LoadAsync(authorizationService, context.User, storeId, pageId);
        var change = new SiteBuilderChange
        {
            Action = "discard_draft",
            ObjectType = ChangePreview.FilePage,
            ObjectId = page.Path,
            ObjectName = page.PageId,
            PageId = page.PageId,
        };

        FileDraftDiscardOperationRequest BlobRequest(bool isDryRun) => new()
        {
            StoreId = page.StoreId,
            ContentType = page.ContentType,
            Path = page.Path,
            SaveId = Guid.NewGuid().ToString("N"),
            ExpectedDraftVersion = isDryRun ? null : ReviewedVersions.Of(change, page.PageId),
            DryRun = isDryRun,
            ReportImpact = true,
        };

        var blob = await FilePages.RunAsync(logger, () => filePageOperationService.DiscardDraftAsync(BlobRequest(true), context.User, cancellationToken));
        if (blob.Status == SiteBuilderOperationStatus.Invalid && blob.Value?.KeptInGit == true)
        {
            await SiteBuilderChanges.RunAsync(change,
                isDryRun => FilePages.RunAsync(logger, () => filePageOperationService.DiscardGitDraftAsync(new FilePageDraftOperationRequest
                {
                    StoreId = page.StoreId,
                    ContentType = page.ContentType,
                    Path = page.Path,
                    ExpectedDraftHead = isDryRun ? null : ReviewedVersions.Of(change, page.PageId),
                    DryRun = isDryRun,
                    ReportImpact = true,
                }, context.User, cancellationToken)),
                x => EnsureFileDiscarded(x, change, page, onlyCopy: IsOnlyCopy(x.Preview)),
                dryRun,
                includeWarnings: true,
                checkAsync: x => PinAsync(change, x.Preview?.Objects.Select(item => Version(page.PageId, item.DraftHead)), expectedVersions, dryRun));

            return change;
        }

        await SiteBuilderChanges.RunAsync(change,
            isDryRun => FilePages.RunAsync(logger, () => filePageOperationService.DiscardDraftAsync(BlobRequest(isDryRun), context.User, cancellationToken)),
            x => EnsureFileDiscarded(x, change, page, onlyCopy: x is SiteBuilderOperationResult<FileDraftDiscard> { Status: SiteBuilderOperationStatus.Invalid, Value.Status: DraftDiscardStatus.OnlyCopy }),
            dryRun,
            checkAsync: x => PinAsync(change, x.Preview?.Objects.Select(item => Version(page.PageId, item.ETag)), expectedVersions, dryRun));

        return change;
    }

    // A draft branch of a page the base branch does not hold is all there is of the page.
    private static bool IsOnlyCopy(SiteBuilderChangePreview preview) =>
        preview?.Objects.Any(x => x.Fields.Any(field => field.Name == ChangePreview.Content && field.To == null)) == true;

    private static Task PinAsync(SiteBuilderChange change, IEnumerable<KeyValuePair<string, string>> versions, IDictionary<string, string> expectedVersions,
        bool dryRun)
    {
        ReviewedVersions.Pin(change, versions ?? [], expectedVersions, dryRun);
        return Task.CompletedTask;
    }

    private static KeyValuePair<string, string> Version(string key, string version) => new(key, version);

    // The operation found the draft at another version than the one pinned: a colleague saved it since the dry run.
    private static void EnsureNotChanged(SiteBuilderOperationResult result, SiteBuilderChange change)
    {
        if (result.Status == SiteBuilderOperationStatus.Conflict)
        {
            throw ReviewedVersions.Changed(change);
        }
    }

    private static void EnsureFileDiscarded(SiteBuilderOperationResult result, SiteBuilderChange change, FilePage page, bool onlyCopy)
    {
        if (onlyCopy)
        {
            throw new McpException($"Page '{page.PageId}' was never published, so its draft is all there is and is not discarded. Edit it with edit_page_draft instead.");
        }

        if (result.Succeeded && result.Preview?.Objects.Count == 0)
        {
            throw new McpException($"Page '{page.PageId}' has no unpublished changes of yours, so there is nothing to discard.");
        }

        EnsureNotChanged(result, change);
        FilePages.EnsureSucceeded(result, page, "discard the draft of");
    }

    private static void EnsureDiscarded(SiteBuilderOperationResult result, SiteBuilderChange change, GroupedSiteBuilderPage group)
    {
        if (result.Status == SiteBuilderOperationStatus.Invalid && group.Pages.All(x => x.Status != PageStatuses.Published))
        {
            throw new McpException($"Page '{group.Id}' was never published, so its draft is all there is and is not discarded. Edit it with edit_page_draft instead.");
        }

        EnsureNotChanged(result, change);
        SiteBuilderChanges.EnsureSucceeded(result, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group));
    }
}
