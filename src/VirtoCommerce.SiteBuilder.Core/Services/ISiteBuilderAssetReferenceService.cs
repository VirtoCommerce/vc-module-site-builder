using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderAssetReferenceService
{
    Task<SiteBuilderAssetReferencesSearchResult> SearchReferencesAsync(SiteBuilderAssetReferencesSearchCriteria criteria, CancellationToken cancellationToken = default);
}
