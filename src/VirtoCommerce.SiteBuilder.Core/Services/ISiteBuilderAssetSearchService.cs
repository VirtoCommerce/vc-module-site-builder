using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderAssetSearchService
{
    Task<SiteBuilderAssetSearchResult> SearchAsync(SiteBuilderAssetSearchCriteria criteria, CancellationToken cancellationToken = default);
}
