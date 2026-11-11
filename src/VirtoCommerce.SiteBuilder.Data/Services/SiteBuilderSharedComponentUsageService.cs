using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Repositories;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class SiteBuilderSharedComponentUsageService(
    Func<ISiteBuilderRepository> repositoryFactory)
    : ISiteBuilderSharedComponentUsageService
{
    public async Task<IList<SiteBuilderSharedComponentUsage>> GetUsageAsync(
        IEnumerable<string> sharedComponentIds,
        string storeId,
        bool includePages = true,
        CancellationToken cancellationToken = default)
    {
        var result = CreateUsageResults(sharedComponentIds);

        if (result.Count == 0 || string.IsNullOrWhiteSpace(storeId))
        {
            return result.Values.ToList();
        }

        using var repository = repositoryFactory();
        var usageQuery = CreateUsageQuery(repository, result.Keys.ToArray(), storeId);

        if (includePages)
        {
            await LoadAndApplyUsagePagesAsync(result, usageQuery, cancellationToken);
        }
        else
        {
            await LoadAndApplyUsageCountsAsync(result, usageQuery, cancellationToken);
        }

        return result.Values.ToList();
    }

    private static Dictionary<string, SiteBuilderSharedComponentUsage> CreateUsageResults(
        IEnumerable<string> sharedComponentIds)
    {
        var ids = sharedComponentIds ?? [];

        return ids
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x,
                x => new SiteBuilderSharedComponentUsage { SharedComponentId = x },
                StringComparer.OrdinalIgnoreCase);
    }

    private static IQueryable<IndexedUsageResult> CreateUsageQuery(
        ISiteBuilderRepository repository,
        string[] sharedComponentIds,
        string storeId)
    {
        return repository.SiteBuilderSharedComponentReferences
            .Where(reference => sharedComponentIds.Contains(reference.SharedComponentId))
            .Join(
                repository.SiteBuilderPages,
                reference => reference.PageId,
                page => page.Id,
                (reference, page) => new { reference, page })
            .Join(
                repository.GroupedSiteBuilderPages,
                x => x.page.GroupId,
                group => group.Id,
                (x, group) => new IndexedUsageResult
                {
                    SharedComponentId = x.reference.SharedComponentId,
                    GroupId = group.Id,
                    StoreId = x.page.StoreId ?? group.StoreId,
                    Name = group.Name,
                    Permalink = group.Permalink,
                    CultureName = group.CultureName,
                    Status = x.page.Status,
                })
            .Where(x => x.StoreId == storeId);
    }

    private static async Task LoadAndApplyUsageCountsAsync(
        Dictionary<string, SiteBuilderSharedComponentUsage> result,
        IQueryable<IndexedUsageResult> usageQuery,
        CancellationToken cancellationToken)
    {
        var usageCounts = await usageQuery
            .Select(x => new { x.SharedComponentId, x.GroupId })
            .Distinct()
            .GroupBy(x => x.SharedComponentId)
            .Select(x => new { SharedComponentId = x.Key, UsageCount = x.Count() })
            .ToListAsync(cancellationToken);

        foreach (var usageCount in usageCounts)
        {
            if (result.TryGetValue(usageCount.SharedComponentId, out var componentUsage))
            {
                componentUsage.UsageCount = usageCount.UsageCount;
            }
        }
    }

    private static async Task LoadAndApplyUsagePagesAsync(
        Dictionary<string, SiteBuilderSharedComponentUsage> result,
        IQueryable<IndexedUsageResult> usageQuery,
        CancellationToken cancellationToken)
    {
        var usages = await usageQuery.ToListAsync(cancellationToken);

        foreach (var componentUsages in usages.GroupBy(x => x.SharedComponentId, StringComparer.OrdinalIgnoreCase))
        {
            if (!result.TryGetValue(componentUsages.Key, out var componentUsage))
            {
                continue;
            }

            componentUsage.Pages = CreateUsagePages(componentUsages);
            componentUsage.UsageCount = componentUsage.Pages.Count;
        }
    }

    private static List<SiteBuilderSharedComponentUsagePage> CreateUsagePages(
        IEnumerable<IndexedUsageResult> usages)
    {
        return usages
            .GroupBy(x => new { x.GroupId, x.Name, x.Permalink, x.CultureName })
            .Select(x => new SiteBuilderSharedComponentUsagePage
            {
                Id = x.Key.GroupId,
                Name = x.Key.Name,
                Permalink = x.Key.Permalink,
                CultureName = x.Key.CultureName,
                Status = string.Join(", ", x.Select(y => y.Status).Distinct().OrderBy(y => y)),
            })
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .ToList();
    }

    private sealed class IndexedUsageResult
    {
        public string SharedComponentId { get; init; }
        public string GroupId { get; init; }
        public string StoreId { get; init; }
        public string Name { get; init; }
        public string Permalink { get; init; }
        public string CultureName { get; init; }
        public string Status { get; init; }
    }
}
