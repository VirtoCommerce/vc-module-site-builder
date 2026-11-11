using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderSharedComponentUsageService
{
    Task<IList<SiteBuilderSharedComponentUsage>> GetUsageAsync(
        IEnumerable<string> sharedComponentIds,
        string storeId,
        bool includePages = true,
        CancellationToken cancellationToken = default);
}
