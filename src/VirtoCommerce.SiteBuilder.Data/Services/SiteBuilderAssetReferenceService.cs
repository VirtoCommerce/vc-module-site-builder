using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.Platform.Core.Common;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class SiteBuilderAssetReferenceService(
    Func<ISiteBuilderRepository> repositoryFactory)
    : ISiteBuilderAssetReferenceService
{
    public Task<SiteBuilderAssetReferencesSearchResult> SearchReferencesAsync(SiteBuilderAssetReferencesSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        return SearchReferencesInternalAsync(criteria, cancellationToken);
    }

    private async Task<SiteBuilderAssetReferencesSearchResult> SearchReferencesInternalAsync(SiteBuilderAssetReferencesSearchCriteria criteria, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(criteria.FolderUrl))
        {
            return await SearchFolderReferencesAsync(criteria, cancellationToken);
        }

        var references = CreateReferences(criteria.AssetUrls);

        if (references.Count == 0 || string.IsNullOrWhiteSpace(criteria.StoreId))
        {
            return CreateResult(references.Values);
        }

        using var repository = repositoryFactory();
        var normalizedAssetUrlHashes = references.Keys
            .Select(SiteBuilderAssetReferenceMatcher.GetAssetUrlHash)
            .ToArray();
        var indexedReferences = ApplyCriteriaFilters(
            CreateIndexedReferencesQuery(repository)
                .Where(reference => normalizedAssetUrlHashes.Contains(reference.NormalizedAssetUrlHash)),
            criteria);
        var referenceGroups = await LoadReferenceGroupsAsync(indexedReferences, cancellationToken);
        var indexedComponentReferences = CreateIndexedComponentReferencesQuery(repository)
            .Where(reference => reference.StoreId == criteria.StoreId)
            .Where(reference => normalizedAssetUrlHashes.Contains(reference.NormalizedAssetUrlHash));
        var componentReferenceGroups = await LoadComponentReferenceGroupsAsync(
            indexedComponentReferences,
            cancellationToken);

        EnrichPageReferenceCounts(references, referenceGroups);
        EnrichComponentReferenceCounts(references, componentReferenceGroups);
        EnrichTotalReferenceCounts(references.Values);

        if (criteria.IncludePages)
        {
            var referencePages = await LoadReferencePagesAsync(indexedReferences, cancellationToken);
            EnrichReferencePages(references, referencePages);

            var referenceComponents = await LoadReferenceComponentsAsync(
                indexedComponentReferences,
                cancellationToken);
            EnrichReferenceComponents(references, referenceComponents);
        }

        return CreateResult(references.Values);
    }

    private async Task<SiteBuilderAssetReferencesSearchResult> SearchFolderReferencesAsync(SiteBuilderAssetReferencesSearchCriteria criteria, CancellationToken cancellationToken)
    {
        var normalizedFolderUrl = SiteBuilderAssetReferenceMatcher.NormalizeAssetFolderUrl(criteria.FolderUrl);

        if (string.IsNullOrWhiteSpace(normalizedFolderUrl) || string.IsNullOrWhiteSpace(criteria.StoreId))
        {
            return CreateResult([]);
        }

        var reference = new SiteBuilderAssetReference
        {
            AssetUrl = criteria.FolderUrl,
            NormalizedAssetUrl = normalizedFolderUrl,
        };

        using var repository = repositoryFactory();
        var folderPrefix = $"{normalizedFolderUrl}/";
        var indexedReferences = ApplyCriteriaFilters(
            CreateIndexedReferencesQuery(repository)
                .Where(x => x.NormalizedAssetUrl.StartsWith(folderPrefix)),
            criteria);
        var referencesCount = await indexedReferences
            .Select(x => x.GroupId)
            .Distinct()
            .CountAsync(cancellationToken);

        reference.PageReferencesCount = referencesCount;

        var indexedComponentReferences = CreateIndexedComponentReferencesQuery(repository)
            .Where(x => x.StoreId == criteria.StoreId)
            .Where(x => x.NormalizedAssetUrl.StartsWith(folderPrefix));
        reference.SharedComponentReferencesCount = await indexedComponentReferences
            .Select(x => x.SharedComponentId)
            .Distinct()
            .CountAsync(cancellationToken);
        reference.ReferencesCount = reference.PageReferencesCount + reference.SharedComponentReferencesCount;

        if (criteria.IncludePages)
        {
            EnrichFolderReferencePages(reference, await LoadReferencePagesAsync(indexedReferences, cancellationToken));
            EnrichFolderReferenceComponents(
                reference,
                await LoadReferenceComponentsAsync(indexedComponentReferences, cancellationToken));
        }

        return CreateResult([reference]);
    }

    private static Dictionary<string, SiteBuilderAssetReference> CreateReferences(IEnumerable<string> assetUrls)
    {
        var normalizedAssets = SiteBuilderAssetReferenceMatcher.NormalizeAssetUrls(assetUrls ?? []);

        return normalizedAssets.ToDictionary(
            x => x.Key,
            x => new SiteBuilderAssetReference
            {
                AssetUrl = x.Value,
                NormalizedAssetUrl = x.Key,
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static IQueryable<IndexedReferenceResult> CreateIndexedReferencesQuery(ISiteBuilderRepository repository)
    {
        var directPageReferences = repository.SiteBuilderAssetReferences
            .Join(
                repository.SiteBuilderPages,
                reference => reference.PageId,
                page => page.Id,
                (reference, page) => new { reference, page })
            .Join(
                repository.GroupedSiteBuilderPages,
                x => x.page.GroupId,
                group => group.Id,
                (x, group) => new IndexedReferenceResult
                {
                    NormalizedAssetUrl = x.reference.NormalizedAssetUrl,
                    GroupId = x.page.GroupId,
                    StoreId = x.page.StoreId ?? group.StoreId,
                    CultureName = group.CultureName,
                    Name = group.Name,
                    Permalink = group.Permalink,
                    Status = x.page.Status,
                    NormalizedAssetUrlHash = x.reference.NormalizedAssetUrlHash,
                });

        // Component assets stay normalized in their aggregate-owned index. Derive the pages that use them
        // at read time instead of copying those assets into SiteBuilderAssetReference. This makes a component
        // content change visible atomically with its asset index and removes any crash window in fan-out.
        var componentDerivedPageReferences = repository.SiteBuilderSharedComponentAssetReferences
            .Join(
                repository.SiteBuilderSharedComponentReferences,
                asset => asset.SharedComponentId,
                placement => placement.SharedComponentId,
                (asset, placement) => new { asset, placement })
            .Join(
                repository.SiteBuilderPages,
                x => x.placement.PageId,
                page => page.Id,
                (x, page) => new { x.asset, page })
            .Join(
                repository.GroupedSiteBuilderPages,
                x => x.page.GroupId,
                group => group.Id,
                (x, group) => new IndexedReferenceResult
                {
                    NormalizedAssetUrl = x.asset.NormalizedAssetUrl,
                    GroupId = x.page.GroupId,
                    StoreId = x.page.StoreId ?? group.StoreId,
                    CultureName = group.CultureName,
                    Name = group.Name,
                    Permalink = group.Permalink,
                    Status = x.page.Status,
                    NormalizedAssetUrlHash = x.asset.NormalizedAssetUrlHash,
                });

        return directPageReferences.Concat(componentDerivedPageReferences);
    }

    private static IQueryable<IndexedComponentReferenceResult> CreateIndexedComponentReferencesQuery(
        ISiteBuilderRepository repository)
    {
        return repository.SiteBuilderSharedComponentAssetReferences
            .Join(
                repository.SiteBuilderSharedComponents,
                reference => reference.SharedComponentId,
                component => component.Id,
                (reference, component) => new IndexedComponentReferenceResult
                {
                    NormalizedAssetUrl = reference.NormalizedAssetUrl,
                    NormalizedAssetUrlHash = reference.NormalizedAssetUrlHash,
                    SharedComponentId = component.Id,
                    Name = component.Name,
                    StoreId = component.StoreId,
                });
    }

    private static IQueryable<IndexedReferenceResult> ApplyCriteriaFilters(
        IQueryable<IndexedReferenceResult> query,
        SiteBuilderAssetReferencesSearchCriteria criteria)
    {
        var allowedStatuses = GetAllowedStatuses(criteria.Statuses).ToArray();

        query = query
            .Where(x => x.StoreId == criteria.StoreId)
            .Where(x => allowedStatuses.Contains(x.Status));

        if (!string.IsNullOrWhiteSpace(criteria.LanguageCode))
        {
            query = query.Where(x => x.CultureName == criteria.LanguageCode);
        }

        if (!criteria.ObjectIds.IsNullOrEmpty())
        {
            query = query.Where(x => criteria.ObjectIds.Contains(x.GroupId));
        }

        return query;
    }

    private static Task<List<ReferenceCountResult>> LoadReferenceGroupsAsync(
        IQueryable<IndexedReferenceResult> indexedReferences,
        CancellationToken cancellationToken)
    {
        return indexedReferences
            .GroupBy(x => x.NormalizedAssetUrl)
            .Select(x => new ReferenceCountResult
            {
                NormalizedAssetUrl = x.Key,
                ReferencesCount = x.Select(r => r.GroupId).Distinct().Count(),
            })
            .ToListAsync(cancellationToken);
    }

    private static Task<List<ComponentReferenceCountResult>> LoadComponentReferenceGroupsAsync(
        IQueryable<IndexedComponentReferenceResult> indexedReferences,
        CancellationToken cancellationToken)
    {
        return indexedReferences
            .GroupBy(x => x.NormalizedAssetUrl)
            .Select(x => new ComponentReferenceCountResult
            {
                NormalizedAssetUrl = x.Key,
                ReferencesCount = x.Select(r => r.SharedComponentId).Distinct().Count(),
            })
            .ToListAsync(cancellationToken);
    }

    private static void EnrichPageReferenceCounts(
        Dictionary<string, SiteBuilderAssetReference> references,
        IEnumerable<ReferenceCountResult> referenceGroups)
    {
        foreach (var group in referenceGroups)
        {
            if (references.TryGetValue(group.NormalizedAssetUrl, out var reference))
            {
                reference.PageReferencesCount = group.ReferencesCount;
            }
        }
    }

    private static void EnrichComponentReferenceCounts(
        Dictionary<string, SiteBuilderAssetReference> references,
        IEnumerable<ComponentReferenceCountResult> referenceGroups)
    {
        foreach (var group in referenceGroups)
        {
            if (references.TryGetValue(group.NormalizedAssetUrl, out var reference))
            {
                reference.SharedComponentReferencesCount = group.ReferencesCount;
            }
        }
    }

    private static void EnrichTotalReferenceCounts(IEnumerable<SiteBuilderAssetReference> references)
    {
        foreach (var reference in references)
        {
            reference.ReferencesCount = reference.PageReferencesCount + reference.SharedComponentReferencesCount;
        }
    }

    private static Task<List<ReferencePageResult>> LoadReferencePagesAsync(
        IQueryable<IndexedReferenceResult> indexedReferences,
        CancellationToken cancellationToken)
    {
        return indexedReferences
            .Select(reference => new ReferencePageResult
            {
                NormalizedAssetUrl = reference.NormalizedAssetUrl,
                Id = reference.GroupId,
                Name = reference.Name,
                Permalink = reference.Permalink,
                CultureName = reference.CultureName,
                Status = reference.Status,
            })
            .ToListAsync(cancellationToken);
    }

    private static Task<List<ReferenceComponentResult>> LoadReferenceComponentsAsync(
        IQueryable<IndexedComponentReferenceResult> indexedReferences,
        CancellationToken cancellationToken)
    {
        return indexedReferences
            .Select(reference => new ReferenceComponentResult
            {
                NormalizedAssetUrl = reference.NormalizedAssetUrl,
                Id = reference.SharedComponentId,
                Name = reference.Name,
            })
            .ToListAsync(cancellationToken);
    }

    private static void EnrichReferencePages(
        Dictionary<string, SiteBuilderAssetReference> references,
        IEnumerable<ReferencePageResult> referencePages)
    {
        foreach (var referencePage in referencePages
            .GroupBy(x => new { x.NormalizedAssetUrl, x.Id, x.Name, x.Permalink, x.CultureName }))
        {
            if (!references.TryGetValue(referencePage.Key.NormalizedAssetUrl, out var reference))
            {
                continue;
            }

            reference.Pages.Add(new SiteBuilderAssetReferencePage
            {
                Id = referencePage.Key.Id,
                Name = referencePage.Key.Name,
                Permalink = referencePage.Key.Permalink,
                CultureName = referencePage.Key.CultureName,
                Status = string.Join(", ", referencePage
                    .Select(x => x.Status)
                    .Distinct()
                    .OrderBy(x => x)),
            });
        }
    }

    private static void EnrichFolderReferencePages(
        SiteBuilderAssetReference reference,
        IEnumerable<ReferencePageResult> referencePages)
    {
        foreach (var referencePage in referencePages
            .GroupBy(x => new { x.Id, x.Name, x.Permalink, x.CultureName }))
        {
            reference.Pages.Add(new SiteBuilderAssetReferencePage
            {
                Id = referencePage.Key.Id,
                Name = referencePage.Key.Name,
                Permalink = referencePage.Key.Permalink,
                CultureName = referencePage.Key.CultureName,
                Status = string.Join(", ", referencePage
                    .Select(x => x.Status)
                    .Distinct()
                    .OrderBy(x => x)),
            });
        }
    }

    private static void EnrichReferenceComponents(
        Dictionary<string, SiteBuilderAssetReference> references,
        IEnumerable<ReferenceComponentResult> referenceComponents)
    {
        foreach (var component in referenceComponents
            .GroupBy(x => new { x.NormalizedAssetUrl, x.Id, x.Name })
            .Select(x => x.Key))
        {
            if (references.TryGetValue(component.NormalizedAssetUrl, out var reference))
            {
                AddReferenceComponent(reference, component.Id, component.Name);
            }
        }
    }

    private static void EnrichFolderReferenceComponents(
        SiteBuilderAssetReference reference,
        IEnumerable<ReferenceComponentResult> referenceComponents)
    {
        foreach (var component in referenceComponents
            .GroupBy(x => new { x.Id, x.Name })
            .Select(x => x.Key))
        {
            AddReferenceComponent(reference, component.Id, component.Name);
        }
    }

    private static void AddReferenceComponent(
        SiteBuilderAssetReference reference,
        string id,
        string name)
    {
        reference.SharedComponents.Add(new SiteBuilderAssetReferenceSharedComponent
        {
            Id = id,
            Name = name,
        });
    }

    private static SiteBuilderAssetReferencesSearchResult CreateResult(IEnumerable<SiteBuilderAssetReference> references)
    {
        var results = references.ToList();

        return new SiteBuilderAssetReferencesSearchResult
        {
            TotalCount = results.Count,
            Results = results,
        };
    }

    private static IEnumerable<string> GetAllowedStatuses(string statuses)
    {
        return GetStatuses(statuses)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeStatus)
            .Where(x => x != null);
    }

    private static string GetStatuses(string statuses)
    {
        return string.IsNullOrWhiteSpace(statuses) ? $"{Draft},{Published}" : statuses;
    }

    private static string NormalizeStatus(string status)
    {
        if (string.Equals(status, Draft, StringComparison.OrdinalIgnoreCase))
        {
            return Draft;
        }

        if (string.Equals(status, Published, StringComparison.OrdinalIgnoreCase))
        {
            return Published;
        }

        if (string.Equals(status, Archived, StringComparison.OrdinalIgnoreCase))
        {
            return Archived;
        }

        return null;
    }

    private sealed class ReferenceCountResult
    {
        public string NormalizedAssetUrl { get; init; }
        public int ReferencesCount { get; init; }
    }

    private sealed class ComponentReferenceCountResult
    {
        public string NormalizedAssetUrl { get; init; }
        public int ReferencesCount { get; init; }
    }

    private sealed class ReferencePageResult
    {
        public string NormalizedAssetUrl { get; init; }
        public string Id { get; init; }
        public string Name { get; init; }
        public string Permalink { get; init; }
        public string CultureName { get; init; }
        public string Status { get; init; }
    }

    private sealed class ReferenceComponentResult
    {
        public string NormalizedAssetUrl { get; init; }
        public string Id { get; init; }
        public string Name { get; init; }
    }

    private sealed class IndexedReferenceResult
    {
        public string NormalizedAssetUrl { get; init; }
        public string NormalizedAssetUrlHash { get; init; }
        public string GroupId { get; init; }
        public string StoreId { get; init; }
        public string CultureName { get; init; }
        public string Name { get; init; }
        public string Permalink { get; init; }
        public string Status { get; init; }
    }

    private sealed class IndexedComponentReferenceResult
    {
        public string NormalizedAssetUrl { get; init; }
        public string NormalizedAssetUrlHash { get; init; }
        public string SharedComponentId { get; init; }
        public string Name { get; init; }
        public string StoreId { get; init; }
    }
}
