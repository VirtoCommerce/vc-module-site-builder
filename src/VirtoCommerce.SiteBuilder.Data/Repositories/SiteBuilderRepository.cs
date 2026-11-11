using System.Data;
using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Domain;
using VirtoCommerce.Platform.Data.Infrastructure;

namespace VirtoCommerce.SiteBuilder.Data.Repositories;

public class SiteBuilderRepository : DbContextRepositoryBase<SiteBuilderDbContext>, ISiteBuilderRepository, ISiteBuilderContentIndexRepository
{
    private const int GroupQueryBatchSize = 500;

    public SiteBuilderRepository(
        SiteBuilderDbContext dbContext,
        IUnitOfWork unitOfWork = null)
        : base(dbContext, unitOfWork)
    {
    }

    public IQueryable<SiteBuilderPageEntity> SiteBuilderPages => DbContext.Set<SiteBuilderPageEntity>();

    public IQueryable<SiteBuilderContentEntity> SiteBuilderContents => DbContext.Set<SiteBuilderContentEntity>();

    public IQueryable<GroupedSiteBuilderPageEntity> GroupedSiteBuilderPages => DbContext.Set<GroupedSiteBuilderPageEntity>();

    public IQueryable<MultilanguagePageEntity> MultilanguagePages => DbContext.Set<MultilanguagePageEntity>();

    public IQueryable<SiteBuilderAssetReferenceEntity> SiteBuilderAssetReferences => DbContext.Set<SiteBuilderAssetReferenceEntity>();

    public IQueryable<SiteBuilderSharedComponentEntity> SiteBuilderSharedComponents => DbContext.Set<SiteBuilderSharedComponentEntity>();

    public IQueryable<SiteBuilderSharedComponentContentEntity> SiteBuilderSharedComponentContents => DbContext.Set<SiteBuilderSharedComponentContentEntity>();

    public IQueryable<SiteBuilderSharedComponentReferenceEntity> SiteBuilderSharedComponentReferences => DbContext.Set<SiteBuilderSharedComponentReferenceEntity>();

    public IQueryable<SiteBuilderSharedComponentAssetReferenceEntity> SiteBuilderSharedComponentAssetReferences => DbContext.Set<SiteBuilderSharedComponentAssetReferenceEntity>();

    public virtual Task RebuildPageContentIndexesAsync(string pageId, string content, string storeId, CancellationToken cancellationToken = default)
    {
        return SiteBuilderPageIndexing.RebuildAfterRawContentWriteAsync(DbContext, pageId, content, storeId, cancellationToken);
    }

    public virtual async Task<IList<SiteBuilderPageEntity>> GetSiteBuilderPagesByIdsAsync(IList<string> ids, string responseGroup)
    {
        if (ids.IsNullOrEmpty())
        {
            return [];
        }

        return ids.Count == 1
            ? await SiteBuilderPages.Where(x => x.Id == ids.First()).ToListAsync()
            : await SiteBuilderPages.Where(x => ids.Contains(x.Id)).ToListAsync();
    }

    public virtual async Task<IList<GroupedSiteBuilderPageEntity>> GetGroupedSiteBuilderPagesByIdsAsync(IList<string> ids, string responseGroup)
    {
        if (ids.IsNullOrEmpty())
        {
            return [];
        }

        var groups = ids.Count == 1
            ? await GroupedSiteBuilderPages.Where(x => x.Id == ids.First()).ToListAsync()
            : await GroupedSiteBuilderPages.Where(x => ids.Contains(x.Id)).ToListAsync();

        if (groups.Count > 0)
        {
            var groupIds = groups.Select(x => x.Id).ToArray();
            await SiteBuilderPages.Where(x => groupIds.Contains(x.GroupId)).LoadAsync();
        }

        return groups;
    }

    public virtual async Task<IList<MultilanguagePageEntity>> GetMultilanguagePagesByIdsAsync(IList<string> ids, string responseGroup)
    {
        if (ids.IsNullOrEmpty())
        {
            return [];
        }

        return ids.Count == 1
            ? await MultilanguagePages.Where(x => x.Id == ids.First()).ToListAsync()
            : await MultilanguagePages.Where(x => ids.Contains(x.Id)).ToListAsync();
    }

    public virtual async Task<IList<SiteBuilderSharedComponentEntity>> GetSiteBuilderSharedComponentsByIdsAsync(
        IList<string> ids,
        string responseGroup)
    {
        if (ids.IsNullOrEmpty())
        {
            return [];
        }

        return ids.Count == 1
            ? await SiteBuilderSharedComponents.Where(x => x.Id == ids.First()).ToListAsync()
            : await SiteBuilderSharedComponents.Where(x => ids.Contains(x.Id)).ToListAsync();
    }

    public virtual Task<bool> ExecuteUnderSharedComponentWriteLockAsync(
        string sharedComponentId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedComponentId);
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteUnderSharedComponentWriteLockCoreAsync(
            sharedComponentId,
            operation,
            cancellationToken);
    }

    public virtual Task<bool> ExecuteUnderSharedComponentWriteLocksAsync(
        IEnumerable<string> sharedComponentIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteUnderSharedComponentWriteLocksCoreAsync(
            sharedComponentIds,
            operation,
            cancellationToken);
    }

    public virtual Task ExecuteUnderPageWriteLocksAsync(
        IEnumerable<string> pageIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteUnderPageWriteLocksCoreAsync(pageIds, operation, cancellationToken);
    }

    public virtual Task ExecuteUnderGroupedPageWriteLocksAsync(
        IEnumerable<string> groupIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteUnderGroupedPageWriteLocksCoreAsync(groupIds, operation, cancellationToken);
    }

    public virtual Task RebuildPageAssetReferenceIndexAsync(
        string pageId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteUnderPageWriteLocksAsync(
            [pageId],
            transactionCancellationToken =>
                SiteBuilderPageIndexing.RebuildCurrentRawPageAssetIndexAsync(
                    DbContext,
                    pageId,
                    transactionCancellationToken),
            cancellationToken);
    }

    private async Task<bool> ExecuteUnderSharedComponentWriteLockCoreAsync(
        string sharedComponentId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (DbContext.Database.CurrentTransaction != null)
        {
            return await ExecuteUnderSharedComponentWriteLockInternalAsync(
                sharedComponentId,
                operation,
                cancellationToken);
        }

        await using var transaction = await DbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var result = await ExecuteUnderSharedComponentWriteLockInternalAsync(
            sharedComponentId,
            operation,
            cancellationToken);

        if (result)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    private async Task<bool> ExecuteUnderSharedComponentWriteLockInternalAsync(
        string sharedComponentId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (!await SiteBuilderWriteLock.AcquireSharedComponentLockAsync(
                DbContext,
                sharedComponentId,
                cancellationToken))
        {
            return false;
        }

        await operation(cancellationToken);
        return true;
    }

    private async Task<bool> ExecuteUnderSharedComponentWriteLocksCoreAsync(
        IEnumerable<string> sharedComponentIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        var orderedIds = SiteBuilderWriteLock.OrderIds(sharedComponentIds);

        if (DbContext.Database.CurrentTransaction != null)
        {
            return await ExecuteUnderSharedComponentWriteLocksInternalAsync(
                orderedIds,
                operation,
                cancellationToken);
        }

        await using var transaction = await DbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var result = await ExecuteUnderSharedComponentWriteLocksInternalAsync(
            orderedIds,
            operation,
            cancellationToken);
        if (result)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    private async Task<bool> ExecuteUnderSharedComponentWriteLocksInternalAsync(
        IEnumerable<string> sharedComponentIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        foreach (var sharedComponentId in sharedComponentIds)
        {
            if (!await SiteBuilderWriteLock.AcquireSharedComponentLockAsync(
                    DbContext,
                    sharedComponentId,
                    cancellationToken))
            {
                return false;
            }
        }

        await operation(cancellationToken);
        return true;
    }

    private async Task ExecuteUnderPageWriteLocksCoreAsync(
        IEnumerable<string> pageIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (DbContext.Database.CurrentTransaction != null)
        {
            await SiteBuilderWriteLock.AcquirePageLocksAsync(
                DbContext,
                pageIds,
                modifiedDate: null,
                cancellationToken);
            await operation(cancellationToken);
            return;
        }

        await using var transaction = await DbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await SiteBuilderWriteLock.AcquirePageLocksAsync(
            DbContext,
            pageIds,
            modifiedDate: null,
            cancellationToken);
        await operation(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ExecuteUnderGroupedPageWriteLocksCoreAsync(
        IEnumerable<string> groupIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (DbContext.Database.CurrentTransaction != null)
        {
            await ExecuteUnderGroupedPageWriteLocksInternalAsync(groupIds, operation, cancellationToken);
            return;
        }

        await using var transaction = await DbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await ExecuteUnderGroupedPageWriteLocksInternalAsync(groupIds, operation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ExecuteUnderGroupedPageWriteLocksInternalAsync(
        IEnumerable<string> groupIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        // Lock groups first so concurrent aggregate saves cannot change page membership between discovering
        // the children and locking them. All group saves use this order; content writers lock only their page
        // (then components), so the ordering cannot form a cycle.
        var orderedGroupIds = SiteBuilderWriteLock.OrderIds(groupIds);
        await SiteBuilderWriteLock.AcquireGroupedPageLocksAsync(
            DbContext,
            orderedGroupIds,
            cancellationToken);

        var pageIds = new List<string>();
        foreach (var batch in orderedGroupIds.Chunk(GroupQueryBatchSize))
        {
            pageIds.AddRange(await SiteBuilderPages
                .Where(x => batch.Contains(x.GroupId))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken));
        }

        await SiteBuilderWriteLock.AcquirePageLocksAsync(
            DbContext,
            pageIds,
            modifiedDate: null,
            cancellationToken);
        await operation(cancellationToken);
    }

}
