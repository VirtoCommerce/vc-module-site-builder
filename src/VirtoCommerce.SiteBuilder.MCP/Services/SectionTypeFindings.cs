using System.Security.Claims;
using Microsoft.Extensions.Logging;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using static VirtoCommerce.SiteBuilder.MCP.Services.PublishChecks;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

// The storefront renders a custom section type only from the published theme: a page published with one the theme holds only as a
// draft, or not at all, shows nothing in its place.
public class SectionTypeFindings(
    ICustomSectionService customSectionService,
    IThemeOperationService themeOperationService,
    IGroupedPageContentService pageContentService,
    ISiteBuilderContentQueryService contentQueryService,
    ILogger<SectionTypeFindings> logger)
{
    private const string SectionTypeMissing = "section-type-missing";
    private const string SectionTypeUnpublished = "section-type-unpublished";

    /// <summary>
    /// The findings of the custom section types an edit adds to a page that the theme does not hold, or holds only as a draft: told as the
    /// section is added, since the page is published all the same.
    /// </summary>
    public async Task<IList<McpFinding>> GetAddedAsync(ClaimsPrincipal user, string storeId, string pageName, PageDocumentEditor before,
        PageDocumentEditor after, GroupedSiteBuilderPage group, CancellationToken cancellationToken)
    {
        var added = GetPlacedTypes(after);
        added.ExceptWith(GetPlacedTypes(before));

        return (await GetAsync(user, storeId, pageName, added, group, cancellationToken))
            .Where(x => x.Id is SectionTypeMissing or SectionTypeUnpublished)
            .ToList();
    }

    /// <summary>The section type findings of a page's draft, as <see cref="PublicationDryRuns.CheckSectionTypesAsync"/> adds them.</summary>
    public async Task<IList<McpFinding>> GetAsync(ClaimsPrincipal user, GroupedSiteBuilderPage group, CancellationToken cancellationToken)
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

        return await GetAsync(user, group.StoreId, group.Name, content, group, cancellationToken);
    }

    /// <summary>The section type findings of a page stored as a file, as <see cref="PublicationDryRuns.CheckFilePageAsync"/> adds them.</summary>
    public async Task<IList<McpFinding>> GetAsync(ClaimsPrincipal user, FilePage page, CancellationToken cancellationToken)
    {
        var draft = await FilePages.ReadAsync(contentQueryService, logger, user, page, draft: true, cancellationToken);

        return await GetAsync(user, page.StoreId, page.PageId, draft, group: null, cancellationToken);
    }

    public Task<IList<McpFinding>> GetAsync(ClaimsPrincipal user, string storeId, string pageName, string content, GroupedSiteBuilderPage group,
        CancellationToken cancellationToken) =>
        GetAsync(user, storeId, pageName, GetPlacedTypes(PageDocumentEditor.Read(content)), group, cancellationToken);

    private async Task<IList<McpFinding>> GetAsync(ClaimsPrincipal user, string storeId, string pageName, HashSet<string> placed,
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

    private static HashSet<string> GetPlacedTypes(PageDocumentEditor document) =>
        (document?.Sections ?? [])
            .Select(SiteBuilderPageItems.GetItemType)
            .Where(x => x != null && ThemeStorage.IsCustomSectionType(x))
            .ToHashSet(StringComparer.Ordinal);
}
