using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.MCP.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Publishing changes what customers see: the write waits for approval and takes exactly the versions its dry run answered (ReviewedVersions).
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
    // A key of expected_versions: the version of the settings of a database page the call publishes.
    private const string SettingsKeyPrefix = "settings:";

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
        "second person's approval and publishes exactly the drafts that were reviewed, to the audience and between the dates they " +
        "were reviewed with; if a draft or a database page's settings change in between, it is refused and nothing is published. " +
        "A call without expected_versions is refused. Pass page_id for a Landing " +
        "Page or one language version, or multilanguage_page_id with languages (or all_languages) for language versions " +
        "of a Multilanguage Page. The page is shown to its audience between its start and end dates, which " +
        "change_page_settings sets. A page stored as a file is named by its path with store_id; in a store that keeps " +
        "its pages in git the reviewed commit of the draft is merged by a pull request (later commits to the draft are not), " +
        "and the answer's shipment tells whether it merged, waits " +
        "for checks, or conflicts. A conflict publishes nothing: re-read the page and apply the change again, or, if the " +
        "user agrees to replace what changed on the published page, call again with rebase: true (a new dry run and approval). " +
        "A database page is refused while other published pages serve its permalink to the same visitors, since it would replace " +
        "them; the refusal names them: unpublish them first, or, if the user agrees to replace them, call again with " +
        "unpublish_conflicting: true (a new dry run and approval). Returns what is (or would be) published.")]
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
        [Description("With page_id of a database page or with languages, only after a publish named the published pages it would replace " +
            "and the user agreed to replace them: unpublish those pages with this publication. Its dry run lists them. Never retry with it " +
            "on your own.")] bool unpublish_conflicting = false,
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

            if (PageLookup.IsFilePagePath(page_id))
            {
                EnsureNoUnpublishConflicting(unpublish_conflicting, "publishing a page stored as a file does not check the permalinks of other pages");
                return await filePageShipping.ShipAsync(context.User,
                    new FilePageShipCall(store_id, page_id, Publish: true, rebase, draft_owner, expected_versions, dryRun), cancellationToken);
            }

            EnsureNoRebase(rebase);
            return await PublishGroupAsync(context.User, page_id, publish: true, expected_versions, unpublish_conflicting, dryRun, cancellationToken);
        }

        EnsureNoRebase(rebase);
        if (all_languages == languages?.Length > 0)
        {
            throw new McpException("With multilanguage_page_id, pass either languages or all_languages: true.");
        }

        if (all_languages)
        {
            EnsureNoUnpublishConflicting(unpublish_conflicting, "publishing every language skips a version that would replace a published page; " +
                "publish that version with languages");
        }

        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, context.User, multilanguage_page_id);

        return all_languages
            ? await PublishAllLanguagesAsync(context.User, root, expected_versions, dryRun, cancellationToken)
            : await PublishLanguagesAsync(context.User, root, languages, expected_versions, unpublish_conflicting, dryRun, cancellationToken);
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
            : PublishGroupAsync(context.User, page_id, publish: false, expected_versions, unpublishConflicting: false, dryRun, cancellationToken);
    }

    // A call that names the versions it takes waits for a second person; one without them is a dry run or is refused.
    public static bool NeedsApproval(IReadOnlyDictionary<string, JsonElement> arguments) => ReviewedVersions.AreGiven(arguments);

    private static void EnsureNoRebase(bool rebase)
    {
        if (rebase)
        {
            throw new McpException("rebase goes with a page stored as a file in git; a database page has no merge to conflict.");
        }
    }

    private static void EnsureNoUnpublishConflicting(bool unpublishConflicting, string reason)
    {
        if (unpublishConflicting)
        {
            throw new McpException($"{PermalinkConflicts.Argument} goes with page_id of a database page or with languages: {reason}.");
        }
    }

    private async Task<SiteBuilderChange> PublishGroupAsync(ClaimsPrincipal user, string pageId, bool publish, IDictionary<string, string> expectedVersions,
        bool unpublishConflicting, bool dryRun, CancellationToken cancellationToken)
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
            isDryRun => RunPublishAsync(user, group.Id, publish, isDryRun, isDryRun ? null : change,
                unpublishConflicting && !isDryRun
                    ? ReplacedVersions(change, change.ExpectedVersions.Keys.Where(x => x != group.Id && x != SettingsKey(group.Id)))
                    : null,
                unpublishConflicting, cancellationToken),
            x => EnsurePublished(x, group, publish),
            dryRun,
            checkAsync: x => PinAsync(change, x.Preview?.Objects, publish ? [group.Id] : [], expectedVersions, dryRun));

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
                ExpectedDraftVersions = isDryRun ? null : new Dictionary<string, string>(change.ExpectedVersions.Where(x => !IsSettingsKey(x.Key))),
                ExpectedSettingsVersions = isDryRun
                    ? null
                    : change.ExpectedVersions.Where(x => IsSettingsKey(x.Key)).ToDictionary(x => x.Key[SettingsKeyPrefix.Length..], x => x.Value),
                DryRun = isDryRun,
                ReportImpact = true,
            }, user, cancellationToken),
            x => EnsureLanguagesPublished(x, change, root),
            dryRun,
            checkAsync: async x =>
            {
                await PinAsync(change, x.Preview?.Objects, x.Preview?.Objects.Select(y => y.ObjectId) ?? [], expectedVersions, dryRun);
                if (dryRun)
                {
                    await WarnOfSkippedVersionsAsync(user, change, x.Preview?.Objects ?? [], cancellationToken);
                }
            });

        foreach (var skipped in result.Value?.Skipped ?? [])
        {
            change.AddWarning(skipped.Reason switch
            {
                MultilanguagePageBatchResult.NoDraftReason => $"{skipped.CultureName}: no draft, nothing to publish.",
                MultilanguagePageBatchResult.ChangedReason => $"{skipped.CultureName}: its draft changed since it was reviewed, or its settings did, so it was not " +
                    "published. Call publish_page with dryRun again for it, show the user the change, and send the new expected_versions.",
                MultilanguagePageBatchResult.PermalinkConflictReason => $"{skipped.CultureName}: another published page serves its permalink to " +
                    $"the same visitors, so it was not published. Call publish_page with languages: ['{skipped.CultureName}'] and dryRun to see the " +
                    "page it would replace, and let the user choose whether to replace it.",
                _ => $"{skipped.CultureName}: publishing failed; the version is as it was. Try it again with languages: ['{skipped.CultureName}'].",
            });
        }

        return change;
    }

    private async Task WarnOfSkippedVersionsAsync(ClaimsPrincipal user, SiteBuilderChange change, IEnumerable<SiteBuilderObjectChange> drafts,
        CancellationToken cancellationToken)
    {
        foreach (var draft in drafts)
        {
            var conflicts = await groupedPageOperationService.FindPermalinkConflictsAsync(user, draft.ObjectId, cancellationToken);
            if (conflicts.Count > 0 && await groupedPageService.GetByIdAsync(draft.ObjectId) is { } group)
            {
                change.AddWarning($"{group.CultureName}: publishing every language skips this version, since published pages serve its permalink to " +
                    $"the same visitors and it would replace them: {PermalinkConflicts.Describe(conflicts)}. " +
                    $"{PermalinkConflicts.FixSkippedVersion(group.CultureName, conflicts)}.");
            }
        }
    }

    // Every chosen version is checked before the first is published. The source language goes first, as in publish-all:
    // translations published with it are based on its new revision.
    private async Task<SiteBuilderChange> PublishLanguagesAsync(ClaimsPrincipal user, MultilanguagePage root, string[] languages,
        IDictionary<string, string> expectedVersions, bool unpublishConflicting, bool dryRun, CancellationToken cancellationToken)
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
        var replaced = new Dictionary<string, List<string>>();
        foreach (var group in groups)
        {
            var check = await RunPublishAsync(user, group.Id, publish: true, isDryRun: true, reviewed: null, expectedReplacedVersions: null,
                unpublishConflicting, cancellationToken);
            EnsurePublished(check, group, publish: true);
            change.AddChanges(check.Preview);
            drafts.AddRange(check.Preview?.Objects ?? []);
            replaced[group.Id] = check.Preview?.Objects.Where(x => x.ObjectId != group.Id).Select(x => x.ObjectId).ToList() ?? [];
        }

        change.AffectsPublished = true;
        await PinAsync(change, drafts, groups.Select(x => x.Id), expectedVersions, dryRun);
        if (dryRun)
        {
            return change;
        }

        var published = new List<string>();
        foreach (var group in groups)
        {
            var result = await RunPublishAsync(user, group.Id, publish: true, isDryRun: false, change,
                unpublishConflicting ? ReplacedVersions(change, replaced[group.Id]) : null, unpublishConflicting, cancellationToken);
            if (!result.Succeeded)
            {
                var done = published.Count == 0 ? "No version was published." : $"Published: {string.Join(", ", published)}.";
                var reason = result.Status switch
                {
                    SiteBuilderOperationStatus.Conflict => "its draft or its settings changed since it was reviewed; call publish_page with dryRun again and send the new expected_versions",
                    SiteBuilderOperationStatus.PermalinkConflict => PermalinkConflicts.Explain(result),
                    _ => result.Error ?? result.Status.ToString(),
                };
                throw new McpException($"Publishing the {group.CultureName} version '{group.Id}' failed: {reason}. {done}");
            }

            published.Add(replaced[group.Id].Count == 0
                ? group.CultureName
                : $"{group.CultureName} (and unpublished {string.Join(", ", replaced[group.Id].Select(x => $"page '{x}'"))})");
        }

        change.DryRun = false;
        return change;
    }

    // reviewed: the change whose pinned versions the operation compares under its lock; null compares none.
    private Task<SiteBuilderOperationResult> RunPublishAsync(ClaimsPrincipal user, string groupId, bool publish, bool isDryRun, IReviewedChange reviewed,
        IDictionary<string, string> expectedReplacedVersions, bool unpublishConflicting, CancellationToken cancellationToken)
    {
        var expectedVersion = reviewed == null ? null : ReviewedVersions.Of(reviewed, groupId);
        return groupedPageOperationService.PublishAsync(new GroupedPagePublishOperationRequest
        {
            GroupId = groupId,
            Publish = publish,
            SaveId = Guid.NewGuid().ToString("N"),
            ExpectedDraftVersion = publish ? expectedVersion : null,
            ExpectedPublishedVersion = publish ? null : expectedVersion,
            ExpectedSettingsVersion = publish && reviewed != null ? ReviewedVersions.Of(reviewed, SettingsKey(groupId)) : null,
            UnpublishConflicting = unpublishConflicting,
            ExpectedReplacedVersions = expectedReplacedVersions,
            DryRun = isDryRun,
            ReportImpact = true,
        }, user, cancellationToken);
    }

    // The published pages the publish replaces, at their pinned versions: the write is refused once others would be unpublished.
    private static Dictionary<string, string> ReplacedVersions(SiteBuilderChange change, IEnumerable<string> pageIds) =>
        pageIds.ToDictionary(x => x, x => ReviewedVersions.Of(change, x));

    // The content of every draft published and of every page replaced, and the settings of every page published: who sees a page
    // and when (organization, user groups, visibility, dates) are not in its content, and an unpublished page's settings change
    // without approval.
    private async Task PinAsync(SiteBuilderChange change, IEnumerable<SiteBuilderObjectChange> drafts, IEnumerable<string> publishedIds,
        IDictionary<string, string> expectedVersions, bool dryRun)
    {
        var versions = (drafts ?? []).Select(x => new KeyValuePair<string, string>(x.ObjectId, x.ETag)).ToList();
        foreach (var id in publishedIds)
        {
            if (await groupedPageService.GetByIdAsync(id) is { } group)
            {
                versions.Add(new KeyValuePair<string, string>(SettingsKey(id), SiteBuilderContentVersion.CreateForGroupSettings(group)));
            }
        }

        ReviewedVersions.Pin(change, versions, expectedVersions, dryRun);
    }

    private static string SettingsKey(string pageId) => SettingsKeyPrefix + pageId;

    private static bool IsSettingsKey(string key) => key.StartsWith(SettingsKeyPrefix, StringComparison.OrdinalIgnoreCase);

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

        if (result.Status == SiteBuilderOperationStatus.PermalinkConflict)
        {
            throw new McpException($"Page '{group.Id}' was not published: {PermalinkConflicts.Explain(result)}.");
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
