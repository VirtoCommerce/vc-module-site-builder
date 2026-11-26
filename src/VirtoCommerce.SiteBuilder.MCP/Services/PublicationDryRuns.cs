using System.Security.Claims;
using Microsoft.Extensions.Logging;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
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
    ICustomSectionService customSectionService,
    IGroupedPageContentService pageContentService,
    ILogger<PublicationDryRuns> logger)
{
    private const string ContrastMarker = "contrast";
    private const string SectionTypeMissing = "section-type-missing";
    private const string SectionTypeUnpublished = "section-type-unpublished";

    public async Task CheckThemeAsync(ClaimsPrincipal user, string storeId, PublishReadiness readiness, CancellationToken cancellationToken)
    {
        var catalogHash = await schemaCatalogService.GetCurrentHashAsync(storeId, cancellationToken);
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
                $"Store '{storeId}' has no theme drafts.", "Nothing to do; edit_theme_draft, edit_template, edit_template_rules, create_section_type and " +
                "change_section_type make drafts.", "theme"));
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

    // The storefront renders a custom section type only from the published theme: a page published with one the theme holds only as a
    // draft, or not at all, shows nothing in its place.
    public async Task CheckSectionTypesAsync(ClaimsPrincipal user, string storeId, string pageName, string content, PublishReadiness readiness,
        GroupedSiteBuilderPage group, CancellationToken cancellationToken)
    {
        foreach (var finding in await GetSectionTypeFindingsAsync(user, storeId, pageName, content, group, cancellationToken))
        {
            readiness.Findings.Add(finding);
        }
    }

    /// <summary>The section type findings of a page's draft, as <see cref="CheckSectionTypesAsync"/> adds them.</summary>
    public async Task<IList<McpFinding>> GetSectionTypeFindingsAsync(ClaimsPrincipal user, GroupedSiteBuilderPage group, CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = (await pageContentService.LoadGroupContentAsync(group, draft: true, cancellationToken)).Content;
        }
        catch (KeyNotFoundException)
        {
            return [];
        }

        return await GetSectionTypeFindingsAsync(user, group.StoreId, group.Name, content, group, cancellationToken);
    }

    /// <summary>The section type findings of a page stored as a file, as <see cref="CheckFilePageAsync"/> adds them.</summary>
    public async Task<IList<McpFinding>> GetSectionTypeFindingsAsync(ClaimsPrincipal user, FilePage page, CancellationToken cancellationToken)
    {
        var draft = await FilePages.ReadAsync(contentQueryService, logger, user, page, draft: true, cancellationToken);

        return await GetSectionTypeFindingsAsync(user, page.StoreId, page.PageId, draft, group: null, cancellationToken);
    }

    /// <summary>
    /// The findings of the custom section types an edit adds to a page that the theme does not hold, or holds only as a draft: told as the
    /// section is added, since the page is published all the same.
    /// </summary>
    public async Task<IList<McpFinding>> GetAddedSectionTypeFindingsAsync(ClaimsPrincipal user, string storeId, string pageName, PageDocumentEditor before,
        PageDocumentEditor after, GroupedSiteBuilderPage group, CancellationToken cancellationToken)
    {
        var added = GetPlacedTypes(after);
        added.ExceptWith(GetPlacedTypes(before));

        return (await GetSectionTypeFindingsAsync(user, storeId, pageName, added, group, cancellationToken))
            .Where(x => x.Id is SectionTypeMissing or SectionTypeUnpublished)
            .ToList();
    }

    private Task<IList<McpFinding>> GetSectionTypeFindingsAsync(ClaimsPrincipal user, string storeId, string pageName, string content,
        GroupedSiteBuilderPage group, CancellationToken cancellationToken) =>
        GetSectionTypeFindingsAsync(user, storeId, pageName, GetPlacedTypes(PageDocumentEditor.Read(content)), group, cancellationToken);

    private static HashSet<string> GetPlacedTypes(PageDocumentEditor document) =>
        (document?.Sections ?? [])
            .Select(SiteBuilderPageItems.GetItemType)
            .Where(x => x != null && ThemeStorage.IsCustomSectionType(x))
            .ToHashSet(StringComparer.Ordinal);

    private async Task<IList<McpFinding>> GetSectionTypeFindingsAsync(ClaimsPrincipal user, string storeId, string pageName, HashSet<string> placed,
        GroupedSiteBuilderPage group, CancellationToken cancellationToken)
    {
        var findings = new List<McpFinding>();
        if (placed.Count == 0)
        {
            return findings;
        }

        var types = (await customSectionService.ListTypesAsync(storeId, themeName: null, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var missing = placed.Where(x => !types.Contains(x)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            findings.Add(Warning(SectionTypeMissing, $"Page '{CustomSectionText.Quote(pageName)}' places custom section types the theme does not hold " +
                $"({string.Join(", ", missing)}): it can still be published, but the storefront shows nothing in their place.",
                $"Store '{storeId}': {string.Join(", ", missing.Select(ThemeStorage.GetCustomSectionFile))} do not exist.",
                "Create them with create_section_type and publish the theme, or remove those sections from the page.", "theme", group));
        }

        var status = await themeOperationService.GetStatusAsync(new SiteBuilderOperationRequest { StoreId = storeId }, user, cancellationToken);
        SiteBuilderChanges.EnsureSucceeded(status, $"The theme of store '{storeId}'", $"Your builder:read permission does not cover store '{storeId}'.");
        var drafts = placed
            .Where(x => types.Contains(x) && status.Value.NewDrafts.Contains(ThemeStorage.GetCustomSectionFile(x), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (drafts.Count > 0)
        {
            findings.Add(Warning(SectionTypeUnpublished, $"Page '{CustomSectionText.Quote(pageName)}' places custom section types the theme holds only as a draft " +
                $"({string.Join(", ", drafts)}): it can still be published, but the storefront shows nothing in their place until the theme is published.",
                $"Store '{storeId}': {string.Join(", ", drafts.Select(ThemeStorage.GetCustomSectionFile))} have no published version.",
                "Publish the theme first (publish_theme, which a second person approves), then the page.", "theme", group));
        }

        // a published type with a draft: the page shows its published version, which may not read settings the page now sets
        var changed = placed
            .Where(x => types.Contains(x) && !drafts.Contains(x) && status.Value.Drafts.Contains(ThemeStorage.GetCustomSectionFile(x), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (changed.Count > 0)
        {
            findings.Add(Warning("section-type-changed", $"Page '{CustomSectionText.Quote(pageName)}' places custom section types with unpublished changes in the theme " +
                $"({string.Join(", ", changed)}): the storefront shows their published version, without what the draft changes or adds, until the theme " +
                "is published.",
                $"Store '{storeId}': {string.Join(", ", changed.Select(ThemeStorage.GetCustomSectionFile))} have a draft.",
                "Publish the theme first (publish_theme, which a second person approves), then the page, when the page relies on those changes.", "theme", group));
        }

        return findings;
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
