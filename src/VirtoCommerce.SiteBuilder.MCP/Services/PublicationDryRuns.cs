using System.Security.Claims;
using Microsoft.Extensions.Logging;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public class PublicationDryRuns(
    IGroupedPageOperationService groupedPageOperationService,
    IMultilanguagePageOperationService multilanguagePageOperationService,
    IFilePageOperationService filePageOperationService,
    IThemeOperationService themeOperationService,
    ISiteBuilderContentQueryService contentQueryService,
    ISchemaCatalogService schemaCatalogService,
    ILogger<PublicationDryRuns> logger)
{
    private const string ContrastMarker = "contrast";

    public async Task CheckThemeAsync(ClaimsPrincipal user, string storeId, PublishReadiness readiness, CancellationToken cancellationToken)
    {
        var catalog = await ThemeFiles.GetCatalogAsync(schemaCatalogService, storeId, cancellationToken);
        var catalogHash = ThemeFiles.GetCatalogHash(catalog);
        if (catalogHash == null)
        {
            readiness.Findings.Add(Blocker("schema-catalog-missing", "The storefront has not sent its schema catalog, so the theme cannot be checked or published.",
                $"Store '{storeId}' has no current schema catalog.", "Open the store once in the designer: its preview sends the catalog. Then check again.", "catalog"));
            return;
        }

        var check = await themeOperationService.PublishAsync(new ThemePublishOperationRequest { StoreId = storeId, CatalogHash = catalogHash, DryRun = true },
            user, cancellationToken);
        EnsureAllowed(check, $"Your builder:read permission does not cover store '{storeId}'.");

        if (!check.Succeeded)
        {
            AddThemeRefusals(check, readiness);
            return;
        }

        AddThemeDryRun(check, storeId, readiness);
    }

    private static void AddThemeDryRun(SiteBuilderOperationResult check, string storeId, PublishReadiness readiness)
    {
        foreach (var draft in check.Preview?.Objects ?? [])
        {
            readiness.Parts.Add(PublishPart.FromPreview(draft));
        }

        foreach (var warning in check.Preview?.Warnings ?? [])
        {
            readiness.Findings.Add(Warning("theme-warning", warning, "The dry run of the theme's publication.", "Review it before publishing; it does not stop the publication.", "theme"));
        }

        if (readiness.Parts.Count == 0)
        {
            readiness.Findings.Add(Info("nothing-to-publish", "The theme has no drafts: a publication would put out the live files again.",
                $"Store '{storeId}' has no theme drafts.", "Nothing to do; edit_theme_draft, edit_template or edit_template_rules make drafts.", "theme"));
        }
    }

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
            readiness.Findings.Add(Blocker("no-draft", $"No language version of Multilanguage Page '{root.Name}' has a draft, so nothing would be published.",
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
                part.Changes = PublishPart.Compare(await FilePages.ReadAsync(contentQueryService, logger, user, page, draft: false, cancellationToken),
                    await FilePages.ReadAsync(contentQueryService, logger, user, page, draft: true, cancellationToken));
            }

            readiness.Parts.Add(part);
        }
    }

    private static void EnsureAllowed(SiteBuilderOperationResult result, string forbidden)
    {
        if (result.Status == SiteBuilderOperationStatus.Forbidden)
        {
            throw new McpForbiddenException(forbidden);
        }
    }

    private static void AddThemeRefusals(SiteBuilderOperationResult check, PublishReadiness readiness)
    {
        var errors = check.Errors?.Count > 0 ? check.Errors : [check.Error ?? check.Status.ToString()];
        foreach (var error in errors)
        {
            readiness.Findings.Add(ThemeRefusal(error));
        }
    }

    private static McpFinding ThemeRefusal(string error)
    {
        if (error.Contains(ContrastMarker, StringComparison.OrdinalIgnoreCase))
        {
            return Blocker("palette-contrast", "A color pair of the palette is below the WCAG 2.2 AA contrast, so the theme cannot be published.", error,
                "Fix the palette with edit_theme_draft (part palette): regenerate it or change the failing colors.", "contrast");
        }

        return Blocker("theme-refused", "The theme's publication would be refused.", error, "Fix the draft it names, then check again.", "validation");
    }

    private static McpFinding NoDraft(GroupedSiteBuilderPage group) =>
        Blocker("no-draft", $"Page '{group.Name}' has no draft, so publishing it would change nothing.", $"Page '{group.Id}' is {group.Status} without unpublished changes.",
            "Edit it with edit_page_draft first, or there is nothing to publish.", "content", group);

    private static McpFinding PermalinkConflict(GroupedSiteBuilderPage group, IList<SiteBuilderPermalinkUsage> conflicts) =>
        Blocker("permalink-conflict", $"Published pages serve the permalink '{group.Permalink}' to the same visitors, so publishing page '{group.Name}' " +
            "would replace them.", PermalinkConflicts.Describe(conflicts), $"{PermalinkConflicts.Fix(conflicts)}.", "address", group);

    private static McpFinding UnpublishRefusal(GroupedSiteBuilderPage group)
    {
        var published = group.Pages.Any(x => x.Status == PageStatuses.Published);
        if (!published)
        {
            return Blocker("not-published", $"Page '{group.Name}' is not published, so there is nothing to unpublish.", $"Page '{group.Id}' is {group.Status}.",
                "Nothing to do.", "rules", group);
        }

        return Blocker("unpublish-refused-while-draft", $"Page '{group.Name}' has unpublished changes, and a page with a draft cannot be unpublished.",
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

    public static McpFinding Blocker(string id, string title, string evidence, string fix, string category, GroupedSiteBuilderPage group = null) =>
        Finding(McpFindingSeverities.Critical, id, title, evidence, fix, category, group);

    public static McpFinding Warning(string id, string title, string evidence, string fix, string category, GroupedSiteBuilderPage group = null) =>
        Finding(McpFindingSeverities.Warning, id, title, evidence, fix, category, group);

    public static McpFinding Info(string id, string title, string evidence, string fix, string category, GroupedSiteBuilderPage group = null) =>
        Finding(McpFindingSeverities.Info, id, title, evidence, fix, category, group);

    private static McpFinding Finding(string severity, string id, string title, string evidence, string fix, string category, GroupedSiteBuilderPage group) => new()
    {
        Id = id,
        Severity = severity,
        Title = title,
        Evidence = evidence,
        Fix = fix,
        Category = category,
        ObjectType = group == null ? null : ChangePreview.Page,
        ObjectId = group?.Id,
        ObjectName = group?.Name,
    };
}
