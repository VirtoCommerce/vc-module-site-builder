using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public static class SiteBuilderPageSelection
{
    private const int ArchivedPriority = 2;

    // Use the same authority and tie-break order for authoring reads, saves, publishing and migration.
    public static IOrderedEnumerable<SiteBuilderPage> Order(IEnumerable<SiteBuilderPage> pages, bool draft = true)
    {
        return pages.Where(x => (draft && x.Status == Draft) || x.Status is Published or Archived)
            .OrderBy(x => x.Status switch { Draft => 0, Published => 1, _ => ArchivedPriority })
            .ThenByDescending(x => x.ModifiedDate).ThenBy(x => x.Id, StringComparer.Ordinal);
    }
}
