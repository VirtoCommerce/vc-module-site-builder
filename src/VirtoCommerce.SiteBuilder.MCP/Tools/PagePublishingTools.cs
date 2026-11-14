using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Publishing and unpublishing change what customers see, so every call waits for a second person. The approver sees
// the dry run of the requester; the change then runs as the approver, through the endpoint's own operation. A page
// stored as a file in git is published by a pull request that may conflict; the way out that replaces the other edit
// (rebase) is an argument of its own, so it is a call of its own with its own approval, and never a retry.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PagePublishingTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IMultilanguagePageService multilanguagePageService,
    IGroupedPageOperationService groupedPageOperationService,
    IMultilanguagePageOperationService multilanguagePageOperationService,
    IFilePageOperationService filePageOperationService,
    ILogger<PagePublishingTools> logger)
{
    [McpServerTool(Name = "publish_page", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Publish)]
    [McpRequiresApproval]
    [Description("Publish the draft of a page, so customers see it; the draft replaces the published version. " +
        "Every call waits for a second person's approval, who sees what the dry run shows. Pass page_id for a Landing " +
        "Page or one language version, or multilanguage_page_id with languages (or all_languages) for language versions " +
        "of a Multilanguage Page. The page is shown to its audience between its start and end dates, which " +
        "change_page_settings sets. A page stored as a file is named by its path with store_id; in a store that keeps " +
        "its pages in git the draft is merged by a pull request, and the answer's shipment tells whether it merged, waits " +
        "for checks, or conflicts. A conflict publishes nothing: re-read the page and apply the change again, or, if the " +
        "user agrees to replace what changed on the published page, call again with rebase: true (a new approval). " +
        "Returns what is (or would be) published.")]
    public async Task<SiteBuilderChange> PublishPage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of a Landing Page or of a language version (search_site_pages), or the path of a page stored as a file " +
            "('pages/about.page'). Pass it or multilanguage_page_id.")] string page_id = null,
        [Description("Id of a Multilanguage Page whose language versions to publish.")] string multilanguage_page_id = null,
        [Description("With multilanguage_page_id: language codes of the versions to publish, for example ['fr-FR']; each must have a draft.")] string[] languages = null,
        [Description("With multilanguage_page_id: true to publish every language version that has a draft.")] bool all_languages = false,
        [Description("With a page stored as a file: the store of the page.")] string store_id = null,
        [Description("With a page stored as a file in git, only after a publish conflicted and the user chose it: put the draft on " +
            "top of the published page, replacing what changed there since the draft began, then merge. Never retry with it on your own.")] bool rebase = false,
        [Description("true to return what would be published, without publishing it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(page_id) == !string.IsNullOrEmpty(multilanguage_page_id))
        {
            throw new McpException("Pass either page_id or multilanguage_page_id.");
        }

        if (!string.IsNullOrEmpty(page_id))
        {
            if (languages?.Length > 0 || all_languages)
            {
                throw new McpException("languages and all_languages go with multilanguage_page_id; page_id is one page already.");
            }

            if (PageLookup.IsFilePagePath(page_id))
            {
                return await ShipFilePageAsync(context.User, store_id, page_id, publish: true, rebase, dryRun, cancellationToken);
            }

            if (rebase)
            {
                throw new McpException("rebase goes with a page stored as a file in git; a database page has no merge to conflict.");
            }

            return await PublishGroupAsync(context.User, page_id, publish: true, dryRun, cancellationToken);
        }

        if (rebase)
        {
            throw new McpException("rebase goes with a page stored as a file in git; a database page has no merge to conflict.");
        }

        if (all_languages == languages?.Length > 0)
        {
            throw new McpException("With multilanguage_page_id, pass either languages or all_languages: true.");
        }

        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, context.User, multilanguage_page_id);

        return all_languages
            ? await PublishAllLanguagesAsync(context.User, root, dryRun, cancellationToken)
            : await PublishLanguagesAsync(context.User, root, languages, dryRun, cancellationToken);
    }

    [McpServerTool(Name = "unpublish_page", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Publish)]
    [McpRequiresApproval]
    [Description("Take a published page off the storefront. Every call waits for a second person's approval. A database " +
        "page's published version becomes the draft; it is refused while the page has unpublished changes: publish them " +
        "(publish_page) or discard them (discard_draft) first. For a Multilanguage Page, unpublish each language version by " +
        "its page_id. A page stored as a file is named by its path with store_id; in git its removal is merged by a pull " +
        "request, and the answer's shipment tells where it stands.")]
    public Task<SiteBuilderChange> UnpublishPage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of a Landing Page or of a language version (search_site_pages), or the path of a page stored as a file ('pages/about.page').")] string page_id,
        [Description("With a page stored as a file: the store of the page.")] string store_id = null,
        [Description("true to return what would be unpublished, without unpublishing it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return PageLookup.IsFilePagePath(page_id)
            ? ShipFilePageAsync(context.User, store_id, page_id, publish: false, rebase: false, dryRun, cancellationToken)
            : PublishGroupAsync(context.User, page_id, publish: false, dryRun, cancellationToken);
    }

    // The operation's dry run, then its write. In git a conflict is an outcome, not a failure: the pull request may be
    // open, so it is answered with the ways out, and nothing is tried again.
    private async Task<SiteBuilderChange> ShipFilePageAsync(ClaimsPrincipal user, string storeId, string pageId, bool publish, bool rebase, bool dryRun,
        CancellationToken cancellationToken)
    {
        var page = await FilePages.LoadAsync(authorizationService, user, storeId, pageId);
        var change = new SiteBuilderChange
        {
            Action = publish ? "publish_page" : "unpublish_page",
            ObjectType = ChangePreview.FilePage,
            ObjectId = page.Path,
            ObjectName = page.PageId,
            PageId = page.PageId,
        };

        var (_, result) = await SiteBuilderChanges.RunAsync(change,
            isDryRun => FilePages.RunAsync(logger, () => publish
                ? filePageOperationService.PublishAsync(new FilePagePublishOperationRequest
                {
                    StoreId = page.StoreId,
                    ContentType = page.ContentType,
                    Path = page.Path,
                    Rebase = rebase,
                    DryRun = isDryRun,
                    ReportImpact = true,
                }, user, cancellationToken)
                : filePageOperationService.UnpublishAsync(new FilePageOperationRequest
                {
                    StoreId = page.StoreId,
                    ContentType = page.ContentType,
                    Path = page.Path,
                    DryRun = isDryRun,
                    ReportImpact = true,
                }, user, cancellationToken)),
            x => EnsureShipped(x, page, publish),
            dryRun,
            includeWarnings: true);

        if (change.DryRun)
        {
            return change;
        }

        change.Shipment = FilePageShipmentRow.From(result.Value);
        switch (result.Value?.State)
        {
            case GitPublishState.Conflict:
                change.AddWarning(result.Value.ConflictError);
                change.AddWarning(result.Value.CanRebase
                    ? "Nothing was published and nothing was tried again. Tell the user the two ways out: re-read the page with " +
                      "search_site_pages, apply the change again with edit_page_draft and publish it; or call publish_page again " +
                      "with rebase: true, which replaces what changed on the published page since this draft began. That call " +
                      "waits for a new approval."
                    : "Nothing was unpublished. Re-read the page with search_site_pages and tell the user; the pull request is in shipment.");
                break;
            case GitPublishState.Pending:
                change.AddWarning("The pull request merges itself once the repository's checks pass; customers see the change after that.");
                break;
            case GitPublishState.AwaitingMerge:
                change.AddWarning("The pull request waits for the repository's checks and does not merge itself: once they pass, " +
                    $"call {change.Action} again (it needs approval again) or merge it in GitHub.");
                break;
        }

        return change;
    }

    private static void EnsureShipped(SiteBuilderOperationResult result, FilePage page, bool publish)
    {
        if (!publish && result.Status == SiteBuilderOperationStatus.NotFound)
        {
            throw new McpException($"Page '{page.PageId}' is not published in store '{page.StoreId}', so there is nothing to unpublish.");
        }

        if (result.Status == SiteBuilderOperationStatus.Invalid && result.Errors?.Count > 0)
        {
            throw new McpException($"Page '{page.PageId}' is not a page document that can be published: {string.Join("; ", result.Errors)}. " +
                "Fix it with edit_page_draft or in the designer.");
        }

        FilePages.EnsureSucceeded(result, page, publish ? "publish" : "unpublish");
    }

    private async Task<SiteBuilderChange> PublishGroupAsync(ClaimsPrincipal user, string pageId, bool publish, bool dryRun, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.LoadPageAsync(authorizationService, user, pageId);
        var change = new SiteBuilderChange
        {
            Action = publish ? "publish_page" : "unpublish_page",
            ObjectType = ChangePreview.Page,
            ObjectId = group.Id,
            ObjectName = group.Name,
            PageId = group.Id,
            MultilanguagePageId = group.MultilanguagePageId,
        };

        await SiteBuilderChanges.RunAsync(change,
            isDryRun => RunPublishAsync(user, group.Id, publish, isDryRun, cancellationToken),
            x => EnsurePublished(x, group, publish),
            dryRun);

        return change;
    }

    private async Task<SiteBuilderChange> PublishAllLanguagesAsync(ClaimsPrincipal user, MultilanguagePage root, bool dryRun, CancellationToken cancellationToken)
    {
        var change = NewMultilanguageChange(root);
        var (_, result) = await SiteBuilderChanges.RunAsync(change,
            isDryRun => multilanguagePageOperationService.PublishAllAsync(new MultilanguagePageOperationRequest { MultilanguagePageId = root.Id, DryRun = isDryRun, ReportImpact = true },
                user, cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root)),
            dryRun);

        foreach (var skipped in result.Value?.Skipped ?? [])
        {
            change.AddWarning(skipped.Reason == MultilanguagePageBatchResult.NoDraftReason
                ? $"{skipped.CultureName}: no draft, nothing to publish."
                : $"{skipped.CultureName}: publishing failed; the version is as it was. Try it again with languages: ['{skipped.CultureName}'].");
        }

        return change;
    }

    // Every chosen version is checked before the first is published. The source language goes first, as in publish-all:
    // translations published with it are based on its new revision.
    private async Task<SiteBuilderChange> PublishLanguagesAsync(ClaimsPrincipal user, MultilanguagePage root, string[] languages, bool dryRun,
        CancellationToken cancellationToken)
    {
        var versions = (await multilanguagePageService.GetLanguagesAsync(root.Id, cancellationToken))
            .Where(x => x.Page != null)
            .ToDictionary(x => x.CultureName, x => x.Page, StringComparer.OrdinalIgnoreCase);

        var missing = languages.Where(x => !versions.ContainsKey(x)).ToList();
        if (missing.Count > 0)
        {
            throw new McpException($"Multilanguage Page '{root.Id}' has no version in {string.Join(", ", missing)}. create_page with kind 'language_version' adds one.");
        }

        var groups = languages
            .Select(x => versions[x])
            .DistinctBy(x => x.Id)
            .OrderByDescending(x => string.Equals(x.CultureName, root.DefaultCultureName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var change = NewMultilanguageChange(root);
        change.DryRun = true;
        foreach (var group in groups)
        {
            var check = await RunPublishAsync(user, group.Id, publish: true, isDryRun: true, cancellationToken);
            EnsurePublished(check, group, publish: true);
            change.AddChanges(check.Preview);
        }

        change.AffectsPublished = true;
        if (dryRun)
        {
            return change;
        }

        var published = new List<string>();
        foreach (var group in groups)
        {
            var result = await RunPublishAsync(user, group.Id, publish: true, isDryRun: false, cancellationToken);
            if (!result.Succeeded)
            {
                var done = published.Count == 0 ? "No version was published." : $"Published: {string.Join(", ", published)}.";
                throw new McpException($"Publishing the {group.CultureName} version '{group.Id}' failed: {result.Error ?? result.Status.ToString()}. {done}");
            }

            published.Add(group.CultureName);
        }

        change.DryRun = false;
        return change;
    }

    private Task<SiteBuilderOperationResult> RunPublishAsync(ClaimsPrincipal user, string groupId, bool publish, bool isDryRun, CancellationToken cancellationToken) =>
        groupedPageOperationService.PublishAsync(new GroupedPagePublishOperationRequest
        {
            GroupId = groupId,
            Publish = publish,
            SaveId = Guid.NewGuid().ToString("N"),
            DryRun = isDryRun,
            ReportImpact = true,
        }, user, cancellationToken);

    private static void EnsurePublished(SiteBuilderOperationResult result, GroupedSiteBuilderPage group, bool publish)
    {
        if (result.Status == SiteBuilderOperationStatus.Invalid)
        {
            var hint = publish
                ? "There is no draft to publish: the page is published as it is."
                : group.Pages.All(x => x.Status != PageStatuses.Published)
                    ? "It is not published."
                    : "It has unpublished changes: publish them with publish_page or discard them with discard_draft first.";
            throw new McpException($"Page '{group.Id}' was not {(publish ? "published" : "unpublished")}. {hint}");
        }

        SiteBuilderChanges.EnsureSucceeded(result, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group));
    }

    private static SiteBuilderChange NewMultilanguageChange(MultilanguagePage root) => new()
    {
        Action = "publish_page",
        ObjectType = ChangePreview.MultilanguagePage,
        ObjectId = root.Id,
        ObjectName = root.Name,
        MultilanguagePageId = root.Id,
    };
}
