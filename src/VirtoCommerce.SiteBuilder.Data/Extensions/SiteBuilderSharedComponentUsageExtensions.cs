using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.Data.Extensions;

public static class SiteBuilderSharedComponentUsageExtensions
{
    /// <summary>
    /// Sets how many pages use each component, and which pages only when <paramref name="includePages"/>: the caller decides whether the user
    /// may see them, as a usage service need not leave them out itself.
    /// </summary>
    public static Task ApplyUsageAsync(this ISiteBuilderSharedComponentUsageService usageService, IList<SiteBuilderSharedComponent> components,
        bool includePages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usageService);
        ArgumentNullException.ThrowIfNull(components);

        return ApplyUsageInternalAsync(usageService, components, includePages, cancellationToken);
    }

    private static async Task ApplyUsageInternalAsync(ISiteBuilderSharedComponentUsageService usageService, IList<SiteBuilderSharedComponent> components,
        bool includePages, CancellationToken cancellationToken)
    {
        foreach (var storeGroup in components.GroupBy(x => x.StoreId, StringComparer.OrdinalIgnoreCase))
        {
            var usages = await usageService.GetUsageAsync(
                storeGroup.Select(x => x.Id),
                storeGroup.Key,
                includePages,
                cancellationToken);
            var usagesById = usages.ToDictionary(x => x.SharedComponentId, StringComparer.OrdinalIgnoreCase);

            foreach (var component in storeGroup)
            {
                if (usagesById.TryGetValue(component.Id, out var usage))
                {
                    component.UsageCount = usage.UsageCount;
                    component.UsagePages = includePages ? usage.Pages : [];
                }
            }
        }
    }
}
