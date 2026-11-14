using System.Text.Json.Nodes;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class SiteBuilderSectionScanner(
    ISiteBuilderPageListService pageListService,
    IGroupedPageContentService pageContentService,
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService,
    IThemeStorageFactory themeStorageFactory)
    : ISiteBuilderSectionScanner
{
    public virtual Task<SiteBuilderSectionScanResult> ScanPagesAsync(SiteBuilderSectionScanCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(criteria.Pages);
        ArgumentException.ThrowIfNullOrWhiteSpace(criteria.SectionType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(criteria.MaxPages);
        ThrowIfInvalid(criteria.Condition);

        return ScanPagesInternalAsync(criteria, cancellationToken);
    }

    public virtual Task<SiteBuilderTemplateSectionScanResult> ScanTemplatesAsync(SiteBuilderTemplateSectionScanCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentException.ThrowIfNullOrWhiteSpace(criteria.StoreId);
        ArgumentException.ThrowIfNullOrWhiteSpace(criteria.SectionType);
        ThrowIfInvalid(criteria.Condition);

        return ScanTemplatesInternalAsync(criteria, cancellationToken);
    }

    protected virtual async Task<SiteBuilderSectionScanResult> ScanPagesInternalAsync(SiteBuilderSectionScanCriteria criteria, CancellationToken cancellationToken)
    {
        var listCriteria = criteria.Pages.CloneTyped();
        listCriteria.Take = criteria.MaxPages;

        var rows = await pageListService.SearchAsync(listCriteria, cancellationToken);
        var result = AbstractTypeFactory<SiteBuilderSectionScanResult>.TryCreateInstance();
        result.TotalPageCount = rows.TotalCount;
        result.Skip = listCriteria.Skip;
        result.ScannedPageCount = rows.Results.Count;

        // A component placed on many pages is read once per scan.
        var components = new Dictionary<string, SiteBuilderPageDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Results)
        {
            foreach (var version in GetVersions(row, criteria.Pages))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.ScannedVersionCount++;
                await ScanVersionAsync(row, version, criteria, components, result, cancellationToken);
            }
        }

        return result;
    }

    // A Landing Page is its own version; a Multilanguage Page is scanned language by language, only those the
    // language and status filters name, since the list keeps a Multilanguage Page when any of its versions matches.
    protected virtual IEnumerable<GroupedSiteBuilderPage> GetVersions(SiteBuilderPageListItem row, SiteBuilderPageSearchCriteria criteria)
    {
        if (row.Page != null)
        {
            return [row.Page];
        }

        var statuses = criteria.Statuses?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        return (row.Languages ?? [])
            .Select(x => x.Page)
            .Where(x => x != null)
            .Where(x => string.IsNullOrEmpty(criteria.LanguageCode) || string.Equals(x.CultureName, criteria.LanguageCode, StringComparison.OrdinalIgnoreCase))
            .Where(x => statuses.Length == 0 || HasStatus(x, statuses));
    }

    // The grouped pages filter in memory: Archived when every version is archived, any other status when one is in it.
    protected static bool HasStatus(GroupedSiteBuilderPage page, string[] statuses)
    {
        var pages = page.Pages ?? [];
        var others = statuses.Where(x => x != Archived).ToList();

        return (statuses.Contains(Archived) && pages.All(x => x.Status == Archived)) || pages.Any(x => others.Contains(x.Status));
    }

    protected virtual async Task ScanVersionAsync(SiteBuilderPageListItem row, GroupedSiteBuilderPage page, SiteBuilderSectionScanCriteria criteria,
        Dictionary<string, SiteBuilderPageDocument> components, SiteBuilderSectionScanResult result, CancellationToken cancellationToken)
    {
        SiteBuilderContentSnapshot snapshot;
        try
        {
            snapshot = await pageContentService.LoadGroupContentAsync(page, draft: !criteria.Published, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            result.Unread.Add(Unread(page, SiteBuilderSectionScanSkip.Deleted));
            return;
        }

        // Without a published version the published read answers an archived one, which the storefront does not show.
        if (criteria.Published && page.Pages?.FirstOrDefault(x => x.Id == snapshot.PageId)?.Status != Published)
        {
            result.Unread.Add(Unread(page, SiteBuilderSectionScanSkip.NotPublished));
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.Content))
        {
            result.Unread.Add(Unread(page, SiteBuilderSectionScanSkip.NoContent));
            return;
        }

        var document = SiteBuilderPageDocument.Read(snapshot.Content);
        if (document == null)
        {
            result.Unread.Add(Unread(page, SiteBuilderSectionScanSkip.NotADocument));
            return;
        }

        var match = AbstractTypeFactory<SiteBuilderSectionScanPage>.TryCreateInstance();
        match.Page = page;
        match.ListItem = row;
        match.ContentPageId = snapshot.PageId;
        match.ETag = snapshot.ETag;
        match.IsLegacyShape = document.IsLegacyShape;

        for (var position = 0; position < document.Sections.Count; position++)
        {
            if (document.Sections[position] is not JsonObject section || IsInGroup(section))
            {
                continue;
            }

            // A page of the older shape cannot hold a placement (SiteBuilderSharedComponentReferenceMatcher reads none).
            if (!document.IsLegacyShape && IsPlacement(section))
            {
                await ScanPlacementAsync(page, section, position, criteria, components, match, cancellationToken);
            }
            else if (SiteBuilderSectionConditions.IsMatch(section, criteria.SectionType, criteria.Condition))
            {
                match.Sections.Add(CreateMatch(section, position));
            }
        }

        if (match.Sections.Count > 0 || match.UnreadSharedComponentIds.Count > 0)
        {
            result.Pages.Add(match);
        }
    }

    protected virtual async Task ScanPlacementAsync(GroupedSiteBuilderPage page, JsonObject placement, int position, SiteBuilderSectionScanCriteria criteria,
        Dictionary<string, SiteBuilderPageDocument> components, SiteBuilderSectionScanPage match, CancellationToken cancellationToken)
    {
        var componentId = GetComponentId(placement);
        if (!components.TryGetValue(componentId, out var component))
        {
            component = await LoadComponentAsync(page.StoreId, componentId, criteria.SharedComponentStoreIds, cancellationToken);
            components[componentId] = component;
        }

        if (component == null)
        {
            if (!match.UnreadSharedComponentIds.Contains(componentId))
            {
                match.UnreadSharedComponentIds.Add(componentId);
            }

            return;
        }

        foreach (var section in component.Sections.OfType<JsonObject>().Where(x => !IsPlacement(x) && !IsInGroup(x)))
        {
            if (SiteBuilderSectionConditions.IsMatch(section, criteria.SectionType, criteria.Condition))
            {
                var result = CreateMatch(section, position);
                result.SharedComponentId = componentId;
                result.PlacementId = GetId(placement);
                match.Sections.Add(result);
            }
        }
    }

    // Null when the component is not one of the page's store, its store is not opened, or its content cannot be read.
    protected virtual async Task<SiteBuilderPageDocument> LoadComponentAsync(string storeId, string componentId, IList<string> storeIds,
        CancellationToken cancellationToken)
    {
        if (storeIds != null && !storeIds.Contains(storeId, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var component = (await sharedComponentService.GetAsync([componentId])).FirstOrDefault();
        if (component == null || !string.Equals(component.StoreId, storeId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return SiteBuilderPageDocument.Read(await sharedComponentContentService.TryLoadContentAsync(component, cancellationToken));
    }

    protected virtual async Task<SiteBuilderTemplateSectionScanResult> ScanTemplatesInternalAsync(SiteBuilderTemplateSectionScanCriteria criteria,
        CancellationToken cancellationToken)
    {
        var storage = await themeStorageFactory.OpenAsync(criteria.StoreId);
        var result = AbstractTypeFactory<SiteBuilderTemplateSectionScanResult>.TryCreateInstance();
        result.ThemeName = storage.ThemeName;

        // A template's draft is listed next to its live file; both name the same template.
        var keys = (await storage.ListFilesAsync(ThemeStorage.TemplatesFolder))
            .Select(x => ThemeStorage.GetTemplateKey(storage.GetLivePath(x)))
            .Where(x => x != null)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = ThemeStorage.GetTemplateFile(key);
            var draft = criteria.Published ? null : await storage.ReadAsync(storage.GetDraftPath(file));
            var content = draft ?? await storage.ReadAsync(file);
            if (content == null)
            {
                // Created in a draft and never published: the published theme has no such template.
                continue;
            }

            result.TemplateCount++;
            var document = SiteBuilderPageDocument.Read(content);
            if (document == null || document.IsLegacyShape)
            {
                result.UnreadTemplateKeys.Add(key);
                continue;
            }

            var template = AbstractTypeFactory<SiteBuilderTemplateSectionScanTemplate>.TryCreateInstance();
            template.Key = key;
            template.Path = file;
            template.IsDraft = draft != null;

            // The storefront shows no Shared Component outside pages, so a placement in a template brings no sections.
            for (var position = 0; position < document.Sections.Count; position++)
            {
                if (document.Sections[position] is JsonObject section && !IsInGroup(section) && !IsPlacement(section) &&
                    SiteBuilderSectionConditions.IsMatch(section, criteria.SectionType, criteria.Condition))
                {
                    template.Sections.Add(CreateMatch(section, position));
                }
            }

            if (template.Sections.Count > 0)
            {
                result.Templates.Add(template);
            }
        }

        return result;
    }

    protected static SiteBuilderSectionMatch CreateMatch(JsonObject section, int position)
    {
        var match = AbstractTypeFactory<SiteBuilderSectionMatch>.TryCreateInstance();
        match.SectionId = GetId(section);
        match.SectionType = GetItemType(section);
        match.Position = position;
        match.Hidden = IsHidden(section);

        return match;
    }

    private static SiteBuilderSectionScanSkip Unread(GroupedSiteBuilderPage page, string reason)
    {
        var skip = AbstractTypeFactory<SiteBuilderSectionScanSkip>.TryCreateInstance();
        skip.PageId = page.Id;
        skip.StoreId = page.StoreId;
        skip.CultureName = page.CultureName;
        skip.Reason = reason;

        return skip;
    }

    private static void ThrowIfInvalid(SiteBuilderSectionSettingCondition condition)
    {
        if (SiteBuilderSectionConditions.Validate(condition) is { } error)
        {
            throw new ArgumentException(error, nameof(condition));
        }
    }
}
