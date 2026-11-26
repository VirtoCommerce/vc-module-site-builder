using System.Security.Claims;
using Microsoft.Extensions.Logging;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.MCP.Services.PublishChecks;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public class PublicationDryRuns(
    IGroupedPageOperationService groupedPageOperationService,
    IMultilanguagePageOperationService multilanguagePageOperationService,
    IFilePageOperationService filePageOperationService,
    ISiteBuilderContentQueryService contentQueryService,
    SectionTypeFindings sectionTypeFindings,
    ILogger<PublicationDryRuns> logger)
{
    public async Task<bool> CheckPageAsync(ClaimsPrincipal user, GroupedSiteBuilderPage group, bool unpublish, PublishReadiness readiness,
        CancellationToken cancellationToken)
    {
        var check = await groupedPageOperationService.PublishAsync(new GroupedPagePublishOperationRequest
        {
            GroupId = group.Id,
            Publish = !unpublish,
            SaveId = Guid.NewGuid().ToString("N"),
            DryRun = true,
        }, user, cancellationToken);

        EnsureAllowed(check, PageLookup.ForbiddenPage(group));
        if (check.Status == SiteBuilderOperationStatus.Invalid)
        {
            readiness.Findings.Add(unpublish ? UnpublishRefusal(group) : NoDraft(group));
            return false;
        }

        if (check.Status == SiteBuilderOperationStatus.PermalinkConflict)
        {
            readiness.Findings.Add(PermalinkConflict(group, PermalinkConflicts.Of(check)));
            return false;
        }

        SiteBuilderChanges.EnsureSucceeded(check, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group));
        if (unpublish)
        {
            readiness.Parts.Add(new PublishPart { ObjectType = ChangePreview.Page, ObjectId = group.Id, Name = group.Name, Language = group.CultureName,
                Changes = [new PartChange("status", PageStatuses.Published, "draft: customers no longer see the page")] });
        }
        else if (group.Pages.Any(x => x.Status == PageStatuses.Published))
        {
            readiness.Findings.Add(Info("unpublish-refused-while-draft", "While the page has this draft, unpublish_page refuses it; publishing the draft lifts that.",
                $"Page '{group.Id}' has a published version and a draft.", "To take the page off instead, discard the draft (discard_draft) and then unpublish it.", "rules"));
        }

        return true;
    }

    public async Task<bool> CheckLanguageVersionAsync(ClaimsPrincipal user, GroupedSiteBuilderPage group, PublishReadiness readiness,
        CancellationToken cancellationToken)
    {
        var conflicts = await groupedPageOperationService.FindPermalinkConflictsAsync(user, group.Id, cancellationToken);
        if (conflicts.Count == 0)
        {
            return true;
        }

        readiness.Findings.Add(Warning("permalink-conflict", $"Publishing every language skips the {group.CultureName} version: published pages serve " +
            $"its permalink '{group.Permalink}' to the same visitors, and it would replace them.", PermalinkConflicts.Describe(conflicts),
            $"{PermalinkConflicts.FixSkippedVersion(group.CultureName, conflicts)}.", "address", group));

        return false;
    }

    public async Task<IList<string>> CheckMultilanguagePageAsync(ClaimsPrincipal user, MultilanguagePage root, PublishReadiness readiness,
        CancellationToken cancellationToken)
    {
        var check = await multilanguagePageOperationService.PublishAllAsync(new MultilanguagePagePublishAllOperationRequest { MultilanguagePageId = root.Id, DryRun = true },
            user, cancellationToken);
        EnsureAllowed(check, PageLookup.ForbiddenMultilanguagePage(root));
        SiteBuilderChanges.EnsureSucceeded(check, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root));

        var groupIds = check.Preview?.Objects.Select(x => x.ObjectId).Where(x => x != null).Distinct().ToList() ?? [];
        if (groupIds.Count == 0)
        {
            readiness.Findings.Add(Blocker("no-draft", $"No language version of Multilanguage Page '{CustomSectionText.Quote(root.Name)}' has a draft, so nothing would be published.",
                $"Multilanguage Page '{root.Id}': every language version is published as it is.", "Edit a language version with edit_page_draft first.", "content"));
        }

        return groupIds;
    }

    public async Task CheckFilePageAsync(ClaimsPrincipal user, FilePage page, bool unpublish, PublishReadiness readiness, CancellationToken cancellationToken)
    {
        var check = await FilePages.RunAsync(logger, () => unpublish
            ? filePageOperationService.UnpublishAsync(new FilePageShipOperationRequest { StoreId = page.StoreId, ContentType = page.ContentType, Path = page.Path, DryRun = true },
                user, cancellationToken)
            : filePageOperationService.PublishAsync(new FilePagePublishOperationRequest { StoreId = page.StoreId, ContentType = page.ContentType, Path = page.Path, DryRun = true },
                user, cancellationToken));

        EnsureAllowed(check, FilePages.Forbidden(page, unpublish ? "unpublish" : "publish"));
        if (!check.Succeeded)
        {
            readiness.Findings.Add(FilePageRefusal(check, page, unpublish));
            return;
        }

        await AddFilePartsAsync(user, page, unpublish, check, readiness, cancellationToken);

        foreach (var warning in check.Preview?.Warnings ?? [])
        {
            readiness.Findings.Add(Info("git-flow", warning, "The dry run of the publication.", "Nothing to do before publishing.", "publishing"));
        }
    }

    private async Task AddFilePartsAsync(ClaimsPrincipal user, FilePage page, bool unpublish, SiteBuilderOperationResult check, PublishReadiness readiness,
        CancellationToken cancellationToken)
    {
        foreach (var item in check.Preview?.Objects ?? [])
        {
            var part = PublishPart.FromPreview(item);
            if (!unpublish)
            {
                var draft = await FilePages.ReadAsync(contentQueryService, logger, user, page, draft: true, cancellationToken);
                part.Changes = PublishPart.Compare(await FilePages.ReadAsync(contentQueryService, logger, user, page, draft: false, cancellationToken), draft);
                await CheckSectionTypesAsync(user, page.StoreId, page.PageId, draft, readiness, group: null, cancellationToken);
            }

            readiness.Parts.Add(part);
        }
    }

    public async Task CheckSectionTypesAsync(ClaimsPrincipal user, string storeId, string pageName, string content, PublishReadiness readiness,
        GroupedSiteBuilderPage group, CancellationToken cancellationToken)
    {
        foreach (var finding in await sectionTypeFindings.GetAsync(user, storeId, pageName, content, group, cancellationToken))
        {
            readiness.Findings.Add(finding);
        }
    }

    private static McpFinding NoDraft(GroupedSiteBuilderPage group) =>
        Blocker("no-draft", $"Page '{CustomSectionText.Quote(group.Name)}' has no draft, so publishing it would change nothing.", $"Page '{group.Id}' is {group.Status} without unpublished changes.",
            "Edit it with edit_page_draft first, or there is nothing to publish.", "content", group);

    private static McpFinding PermalinkConflict(GroupedSiteBuilderPage group, IList<SiteBuilderPermalinkUsage> conflicts) =>
        Blocker("permalink-conflict", $"Published pages serve the permalink '{group.Permalink}' to the same visitors, so publishing page '{CustomSectionText.Quote(group.Name)}' " +
            "would replace them.", PermalinkConflicts.Describe(conflicts), $"{PermalinkConflicts.Fix(conflicts)}.", "address", group);

    private static McpFinding UnpublishRefusal(GroupedSiteBuilderPage group)
    {
        var published = group.Pages.Any(x => x.Status == PageStatuses.Published);
        if (!published)
        {
            return Blocker("not-published", $"Page '{CustomSectionText.Quote(group.Name)}' is not published, so there is nothing to unpublish.", $"Page '{group.Id}' is {group.Status}.",
                "Nothing to do.", "rules", group);
        }

        return Blocker("unpublish-refused-while-draft", $"Page '{CustomSectionText.Quote(group.Name)}' has unpublished changes, and a page with a draft cannot be unpublished.",
            $"Page '{group.Id}' has a published version and a draft.", "Publish the draft (publish_page) or discard it (discard_draft) first, then unpublish.", "rules", group);
    }

    private static McpFinding FilePageRefusal(SiteBuilderOperationResult check, FilePage page, bool unpublish)
    {
        if (check.Status == SiteBuilderOperationStatus.NotFound)
        {
            return unpublish
                ? Blocker("not-published", $"Page '{page.PageId}' is not published, so there is nothing to unpublish.", $"Store '{page.StoreId}'.", "Nothing to do.", "rules")
                : Blocker("page-not-found", $"Page '{page.PageId}' was not found in store '{page.StoreId}'.", "No draft or published file.", "Check the folder and path the designer shows.", "content");
        }

        var details = check.Errors?.Count > 0 ? string.Join("; ", check.Errors) : check.Error;

        return Blocker("page-refused", $"The publication of page '{page.PageId}' would be refused.", details ?? check.Status.ToString(),
            "Fix the page with edit_page_draft or in the designer, then check again.", "validation");
    }
}
