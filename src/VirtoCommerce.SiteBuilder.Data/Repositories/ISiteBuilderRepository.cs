using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Repositories;

public interface ISiteBuilderRepository : IRepository
{
    IQueryable<SiteBuilderPageEntity> SiteBuilderPages { get; }
    IQueryable<SiteBuilderContentEntity> SiteBuilderContents { get; }
    IQueryable<GroupedSiteBuilderPageEntity> GroupedSiteBuilderPages { get; }
    IQueryable<MultilanguagePageEntity> MultilanguagePages { get; }
    IQueryable<SiteBuilderAssetReferenceEntity> SiteBuilderAssetReferences { get; }
    IQueryable<SiteBuilderSharedComponentEntity> SiteBuilderSharedComponents { get; }
    IQueryable<SiteBuilderSharedComponentContentEntity> SiteBuilderSharedComponentContents { get; }
    IQueryable<SiteBuilderSharedComponentReferenceEntity> SiteBuilderSharedComponentReferences { get; }
    IQueryable<SiteBuilderSharedComponentAssetReferenceEntity> SiteBuilderSharedComponentAssetReferences { get; }

    Task<IList<SiteBuilderPageEntity>> GetSiteBuilderPagesByIdsAsync(IList<string> ids, string responseGroup);
    Task<IList<GroupedSiteBuilderPageEntity>> GetGroupedSiteBuilderPagesByIdsAsync(IList<string> ids, string responseGroup);
    Task<IList<MultilanguagePageEntity>> GetMultilanguagePagesByIdsAsync(IList<string> ids, string responseGroup);
    Task<IList<SiteBuilderSharedComponentEntity>> GetSiteBuilderSharedComponentsByIdsAsync(
        IList<string> ids,
        string responseGroup);

    Task<bool> ExecuteUnderSharedComponentWriteLockAsync(
        string sharedComponentId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task<bool> ExecuteUnderSharedComponentWriteLocksAsync(
        IEnumerable<string> sharedComponentIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteUnderPageWriteLocksAsync(
        IEnumerable<string> pageIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteUnderGroupedPageWriteLocksAsync(
        IEnumerable<string> groupIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task RebuildPageAssetReferenceIndexAsync(
        string pageId,
        CancellationToken cancellationToken = default);
}
