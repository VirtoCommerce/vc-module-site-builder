using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
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

// Publishing and unpublishing change what customers see, so they wait for a second person. The approver sees the dry
// run of the requester; the change then runs as the approver, with the requester's arguments, through the endpoint's
// own operation. So the call names what was reviewed: the dry run answers expected_versions (the version of each draft
// or published page it takes; in git the commit of the draft branch) and, in git, draft_owner (whose branch it ships),
// and the call without dryRun sends them back. The write takes exactly those or is refused; a call without them is
// refused and never waits for approval. A page stored as a file in git is published by a pull request that may
// conflict; the way out that replaces the other edit (rebase) is an argument of its own, so it is a call of its own with
// its own approval, and never a retry.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PagePublishingTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IMultilanguagePageService multilanguagePageService,
    IGroupedPageOperationService groupedPageOperationService,
    IMultilanguagePageOperationService multilanguagePageOperationService,
    FilePageShipping filePageShipping)
{
    private const string DraftOwnerDescription = "With a page stored as a file in git: the user whose draft branch is shipped, as the dry run " +
        "answers it in draft_owner (your own when you omit it on the dry run). Send it back unchanged, so that the approver ships the " +
        "reviewed draft and not their own. Not used for other pages.";

    [McpServerTool(Name = "publish_page", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Publish)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Publish the draft of a page, so customers see it; the draft replaces the published version. " +
        "First call it with dryRun: it shows what would be published and answers expected_versions (and, for a page stored as " +
        "a file in git, draft_owner). Then call it without dryRun and with those values unchanged: the publication waits for a " +
        "second person's approval and publishes exactly the drafts that were reviewed; if one changes in between, it is refused " +
        "and nothing is published. A call without expected_versions is refused. Pass page_id for a Landing " +
        "Page or one language version, or multilanguage_page_id with languages (or all_languages) for language versions " +
        "of a Multilanguage Page. The page is shown to its audience between its start and end dates, which " +
        "change_page_settings sets. A page stored as a file is named by its path with store_id; in a store that keeps " +
        "its pages in git the draft is merged by a pull request, and the answer's shipment tells whether it merged, waits " +
        "for checks, or conflicts. A conflict publishes nothing: re-read the page and apply the change again, or, if the " +
        "user agrees to replace what changed on the published page, call again with rebase: true (a new dry run and approval). " +
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
        [Description(ReviewedVersions.Description)] Dictionary<string, string> expected_versions = null,
        [Description(DraftOwnerDescription)] string draft_owner = null,
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

            return PageLookup.IsFilePagePath(page_id)
                ? await filePageShipping.ShipAsync(context.User,
                    new FilePageShipCall(store_id, page_id, Publish: true, rebase, draft_owner, expected_versions, dryRun), cancellationToken)
                : await PublishGroupAsync(context.User, page_id, publish: true, EnsureNoRebase(rebase, expected_versions), dryRun, cancellationToken);
        }

        EnsureNoRebase(rebase, expected_versions);
        if (all_languages == languages?.Length > 0)
        {
            throw new McpException("With multilanguage_page_id, pass either languages or all_languages: true.");
        }

        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, context.User, multilanguage_page_id);

        return all_languages
            ? await PublishAllLanguagesAsync(context.User, root, expected_versions, dryRun, cancellationToken)
            : await PublishLanguagesAsync(context.User, root, languages, expected_versions, dryRun, cancellationToken);
    }

    [McpServerTool(Name = "unpublish_page", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Publish)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Take a published page off the storefront. First call it with dryRun: it answers expected_versions (and, for a " +
        "page stored as a file in git, draft_owner). Then call it without dryRun and with those values unchanged: the call waits " +
        "for a second person's approval and unpublishes the page as it was reviewed; if the page changes in between, it is " +
        "refused. A call without expected_versions is refused. A database page's published version becomes the draft; it is " +
        "refused while the page has unpublished changes: publish them (publish_page) or discard them (discard_draft) first. For " +
        "a Multilanguage Page, unpublish each language version by its page_id. A page stored as a file is named by its path " +
        "with store_id; in git its removal is merged by a pull request, and the answer's shipment tells where it stands.")]
    public Task<SiteBuilderChange> UnpublishPage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of a Landing Page or of a language version (search_site_pages), or the path of a page stored as a file ('pages/about.page').")] string page_id,
        [Description("With a page stored as a file: the store of the page.")] string store_id = null,
        [Description(ReviewedVersions.Description)] Dictionary<string, string> expected_versions = null,
        [Description(DraftOwnerDescription)] string draft_owner = null,
        [Description("true to return what would be unpublished, without unpublishing it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return PageLookup.IsFilePagePath(page_id)
            ? filePageShipping.ShipAsync(context.User,
                new FilePageShipCall(store_id, page_id, Publish: false, Rebase: false, draft_owner, expected_versions, dryRun), cancellationToken)
            : PublishGroupAsync(context.User, page_id, publish: false, expected_versions, dryRun, cancellationToken);
    }

    // A call that names the versions it takes waits for a second person; one without them is a dry run or is refused.
    public static bool NeedsApproval(IReadOnlyDictionary<string, JsonElement> arguments) => ReviewedVersions.AreGiven(arguments);

    private static IDictionary<string, string> EnsureNoRebase(bool rebase, IDictionary<string, string> expectedVersions)
    {
        if (rebase)
        {
            throw new McpException("rebase goes with a page stored as a file in git; a database page has no merge to conflict.");
        }

        return expectedVersions;
    }

    // The version of the draft a publish takes, or of the published page an unpublish takes.
    private async Task<SiteBuilderChange> PublishGroupAsync(ClaimsPrincipal user, string pageId, bool publish, IDictionary<string, string> expectedVersions,
        bool dryRun, CancellationToken cancellationToken)
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
            isDryRun => RunPublishAsync(user, group.Id, publish, isDryRun, ReviewedVersions.Of(change, group.Id), cancellationToken),
            x => EnsurePublished(x, group, publish),
            dryRun,
            checkAsync: x => PinAsync(change, x.Preview?.Objects, expectedVersions, dryRun));

        return change;
    }

    // The draft of every language version that has one, at the version reviewed: a version whose draft appeared, went or
    // changed since refuses the whole publication.
    private async Task<SiteBuilderChange> PublishAllLanguagesAsync(ClaimsPrincipal user, MultilanguagePage root, IDictionary<string, string> expectedVersions,
        bool dryRun, CancellationToken cancellationToken)
    {
        var change = NewMultilanguageChange(root);
        var (_, result) = await SiteBuilderChanges.RunAsync(change,
            isDryRun => multilanguagePageOperationService.PublishAllAsync(new MultilanguagePagePublishAllOperationRequest
            {
                MultilanguagePageId = root.Id,
                ExpectedDraftVersions = isDryRun ? null : new Dictionary<string, string>(change.ExpectedVersions),
                DryRun = isDryRun,
                ReportImpact = true,
            }, user, cancellationToken),
            x => EnsureLanguagesPublished(x, change, root),
            dryRun,
            checkAsync: x => PinAsync(change, x.Preview?.Objects, expectedVersions, dryRun));

        foreach (var skipped in result.Value?.Skipped ?? [])
        {
            change.AddWarning(skipped.Reason switch
            {
                MultilanguagePageBatchResult.NoDraftReason => $"{skipped.CultureName}: no draft, nothing to publish.",
                MultilanguagePageBatchResult.ChangedReason => $"{skipped.CultureName}: its draft changed since it was reviewed, so it was not " +
                    "published. Call publish_page with dryRun again for it, show the user the change, and send the new expected_versions.",
                _ => $"{skipped.CultureName}: publishing failed; the version is as it was. Try it again with languages: ['{skipped.CultureName}'].",
            });
        }

        return change;
    }

    // Every chosen version is checked before the first is published. The source language goes first, as in publish-all:
    // translations published with it are based on its new revision.
    private async Task<SiteBuilderChange> PublishLanguagesAsync(ClaimsPrincipal user, MultilanguagePage root, string[] languages,
        IDictionary<string, string> expectedVersions, bool dryRun, CancellationToken cancellationToken)
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
        var drafts = new List<SiteBuilderObjectChange>();
        foreach (var group in groups)
        {
            var check = await RunPublishAsync(user, group.Id, publish: true, isDryRun: true, expectedVersion: null, cancellationToken);
            EnsurePublished(check, group, publish: true);
            change.AddChanges(check.Preview);
            drafts.AddRange(check.Preview?.Objects ?? []);
        }

        change.AffectsPublished = true;
        await PinAsync(change, drafts, expectedVersions, dryRun);
        if (dryRun)
        {
            return change;
        }

        var published = new List<string>();
        foreach (var group in groups)
        {
            var result = await RunPublishAsync(user, group.Id, publish: true, isDryRun: false, ReviewedVersions.Of(change, group.Id), cancellationToken);
            if (!result.Succeeded)
            {
                var done = published.Count == 0 ? "No version was published." : $"Published: {string.Join(", ", published)}.";
                var reason = result.Status == SiteBuilderOperationStatus.Conflict
                    ? "its draft changed since it was reviewed; call publish_page with dryRun again and send the new expected_versions"
                    : result.Error ?? result.Status.ToString();
                throw new McpException($"Publishing the {group.CultureName} version '{group.Id}' failed: {reason}. {done}");
            }

            published.Add(group.CultureName);
        }

        change.DryRun = false;
        return change;
    }

    // The write takes the version pinned: of the draft for a publish, of the published page for an unpublish.
    private Task<SiteBuilderOperationResult> RunPublishAsync(ClaimsPrincipal user, string groupId, bool publish, bool isDryRun, string expectedVersion,
        CancellationToken cancellationToken)
    {
        return groupedPageOperationService.PublishAsync(new GroupedPagePublishOperationRequest
        {
            GroupId = groupId,
            Publish = publish,
            SaveId = Guid.NewGuid().ToString("N"),
            ExpectedDraftVersion = publish ? expectedVersion : null,
            ExpectedPublishedVersion = publish ? null : expectedVersion,
            DryRun = isDryRun,
            ReportImpact = true,
        }, user, cancellationToken);
    }

    private static Task PinAsync(SiteBuilderChange change, IEnumerable<SiteBuilderObjectChange> drafts, IDictionary<string, string> expectedVersions, bool dryRun)
    {
        ReviewedVersions.Pin(change, (drafts ?? []).Select(x => new KeyValuePair<string, string>(x.ObjectId, x.ETag)), expectedVersions, dryRun);
        return Task.CompletedTask;
    }

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

        if (result.Status == SiteBuilderOperationStatus.Conflict)
        {
            throw new McpException($"Page '{group.Id}' changed since it was reviewed, so it was not {(publish ? "published" : "unpublished")}. Call " +
                $"{(publish ? "publish_page" : "unpublish_page")} with dryRun again, show the user what it would do now, and send the new " +
                "expected_versions for approval.");
        }

        SiteBuilderChanges.EnsureSucceeded(result, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group));
    }

    private static void EnsureLanguagesPublished(SiteBuilderOperationResult result, SiteBuilderChange change, MultilanguagePage root)
    {
        if (result.Status == SiteBuilderOperationStatus.Conflict)
        {
            throw ReviewedVersions.Changed(change);
        }

        SiteBuilderChanges.EnsureSucceeded(result, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root));
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
