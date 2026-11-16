using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.GenericCrud;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public static class GroupedPageSearchServiceExtensions
{
    /// <summary>
    /// Other pages of the same store that use the permalink of <paramref name="page"/>, except archived ones,
    /// with the conflict that publishing <paramref name="page"/> creates with each of them. Pass the store's default
    /// language when it is known: a page for any language competes with a page in that language.
    /// </summary>
    public static async Task<IList<SiteBuilderPermalinkUsage>> FindPermalinkUsagesAsync(
        this IGroupedPageSearchService searchService,
        GroupedSiteBuilderPage page,
        string storeDefaultLanguage = null)
    {
        if (SiteBuilderPermalinkConflicts.Normalize(page.Permalink) == null || string.IsNullOrEmpty(page.StoreId))
        {
            return [];
        }

        var criteria = AbstractTypeFactory<SiteBuilderPageSearchCriteria>.TryCreateInstance();
        criteria.StoreId = page.StoreId;
        criteria.Permalink = page.Permalink;
        var groups = await searchService.SearchAllNoCloneAsync(criteria);

        return groups
            .Where(x => !string.Equals(x.Id, page.Id, StringComparison.OrdinalIgnoreCase) &&
                x.Status != Archived &&
                SiteBuilderPermalinkConflicts.HasSamePermalink(page, x))
            .Select(x => ToPermalinkUsage(page, x, storeDefaultLanguage))
            .ToList();
    }

    private static SiteBuilderPermalinkUsage ToPermalinkUsage(GroupedSiteBuilderPage page, GroupedSiteBuilderPage other,
        string storeDefaultLanguage)
    {
        var usage = AbstractTypeFactory<SiteBuilderPermalinkUsage>.TryCreateInstance();
        usage.GroupId = other.Id;
        usage.Name = other.Name;
        usage.Permalink = other.Permalink;
        usage.CultureName = other.CultureName;
        usage.Status = other.Status;
        usage.Conflict = SiteBuilderPermalinkConflicts.GetConflict(page, other, DateTime.UtcNow, storeDefaultLanguage);
        // Unpublishing is refused while a draft exists alongside the published page.
        usage.CanUnpublish = !other.HasChanges;
        return usage;
    }
}
