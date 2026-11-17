using VirtoCommerce.SiteBuilder.Data.Repositories;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public interface ISiteBuilderSharedComponentAssetReferenceIndexService
{
    Task RebuildIndexInCurrentUnitOfWorkAsync(
        ISiteBuilderRepository repository,
        string sharedComponentId,
        string content,
        CancellationToken cancellationToken = default);
}
