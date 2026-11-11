using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.Platform.Caching;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class SiteBuilderSharedComponentReferenceIndexService(
    Func<ISiteBuilderRepository> repositoryFactory)
    : ISiteBuilderSharedComponentReferenceIndexService
{
    private const int QueryBatchSize = 500;

    public async Task ValidateReferencesForStoreAsync(
        string storeId,
        IEnumerable<string> contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);

        var sharedComponentIds = SiteBuilderWriteLock.OrderIds(
            (contents ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .SelectMany(SiteBuilderSharedComponentReferenceMatcher.ExtractReferences));

        if (sharedComponentIds.Length == 0)
        {
            return;
        }

        using var repository = repositoryFactory();
        await ValidateComponentsAsync(
            repository,
            sharedComponentIds,
            storeId,
            cancellationToken);
    }

    public async Task<IList<string>> GetPageIdsAsync(
        IEnumerable<string> sharedComponentIds,
        CancellationToken cancellationToken = default)
    {
        var ids = sharedComponentIds
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ids.IsNullOrEmpty())
        {
            return [];
        }

        using var repository = repositoryFactory();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in ids.Chunk(QueryBatchSize))
        {
            var pageIds = await repository.SiteBuilderSharedComponentReferences
                .Where(x => batch.Contains(x.SharedComponentId))
                .Select(x => x.PageId)
                .Distinct()
                .ToListAsync(cancellationToken);

            result.UnionWith(pageIds);
        }

        return result.ToList();
    }

    internal static async Task ValidateComponentsAsync(
        ISiteBuilderRepository repository,
        string[] sharedComponentIds,
        string pageStoreId,
        CancellationToken cancellationToken)
    {
        await ValidateComponentsAsync(
            repository.SiteBuilderSharedComponents,
            repository.SiteBuilderSharedComponentContents,
            sharedComponentIds,
            pageStoreId,
            cancellationToken);
    }

    private static async Task ValidateComponentsAsync(
        IQueryable<SiteBuilderSharedComponentEntity> componentsQuery,
        IQueryable<SiteBuilderSharedComponentContentEntity> contentsQuery,
        string[] sharedComponentIds,
        string pageStoreId,
        CancellationToken cancellationToken)
    {
        var componentStores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in sharedComponentIds.Chunk(QueryBatchSize))
        {
            var components = await componentsQuery
                .Where(x => batch.Contains(x.Id))
                .Select(x => new { x.Id, x.StoreId })
                .ToListAsync(cancellationToken);
            var batchContentIds = await contentsQuery
                .Where(x => batch.Contains(x.Id))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            foreach (var component in components)
            {
                componentStores[component.Id] = component.StoreId;
            }

            contentIds.UnionWith(batchContentIds);
        }

        SiteBuilderSharedComponentReferenceValidator.Validate(
            sharedComponentIds,
            pageStoreId,
            componentStores,
            contentIds);
    }
}
