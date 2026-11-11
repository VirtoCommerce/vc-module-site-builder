namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderAssetReferenceIndexService
{
    Task DeletePageIndexAsync(IEnumerable<string> pageIds, CancellationToken cancellationToken = default);

    Task DeleteGroupIndexAsync(IEnumerable<string> groupIds, CancellationToken cancellationToken = default);
}
