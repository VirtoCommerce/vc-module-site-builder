namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderSharedComponentReferenceIndexService
{
    Task ValidateReferencesForStoreAsync(
        string storeId,
        IEnumerable<string> contents,
        CancellationToken cancellationToken = default);

    Task<IList<string>> GetPageIdsAsync(
        IEnumerable<string> sharedComponentIds,
        CancellationToken cancellationToken = default);

}
