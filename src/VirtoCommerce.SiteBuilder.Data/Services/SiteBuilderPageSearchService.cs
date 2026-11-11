using Microsoft.Extensions.Options;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.GenericCrud;
using VirtoCommerce.Platform.Data.GenericCrud;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class SiteBuilderPageSearchService(
    Func<ISiteBuilderRepository> repositoryFactory,
    IPlatformMemoryCache platformMemoryCache,
    ISiteBuilderPageService crudService,
    IOptions<CrudOptions> crudOptions)
    : SearchService<SiteBuilderPageSearchCriteria, SiteBuilderPageSearchResult, SiteBuilderPage, SiteBuilderPageEntity>(
        repositoryFactory, platformMemoryCache, crudService, crudOptions), ISiteBuilderPageSearchService
{
    protected override IQueryable<SiteBuilderPageEntity> BuildQuery(IRepository repository, SiteBuilderPageSearchCriteria criteria)
    {
        var siteBuilderRepository = (ISiteBuilderRepository)repository;
        var query = siteBuilderRepository.SiteBuilderPages;

        if (!string.IsNullOrEmpty(criteria.StoreId))
        {
            query = query.Where(x => x.StoreId == criteria.StoreId);
        }

        if (!string.IsNullOrEmpty(criteria.Statuses))
        {
            var statuses = criteria.Statuses.Split(',', StringSplitOptions.RemoveEmptyEntries);
            query = query.Where(x => statuses.Contains(x.Status));
        }

        if (criteria.ObjectIds is { Count: > 0 })
        {
            query = query.Where(x => criteria.ObjectIds.Contains(x.Id));
        }

        return ApplyDateRange(
            query,
            siteBuilderRepository,
            criteria.ModifiedSince,
            criteria.ModifiedBefore);
    }

    private static IQueryable<SiteBuilderPageEntity> ApplyDateRange(
        IQueryable<SiteBuilderPageEntity> pages,
        ISiteBuilderRepository repository,
        DateTime? modifiedSince,
        DateTime? modifiedBefore)
    {
        if (modifiedSince.HasValue)
        {
            var start = modifiedSince.Value;
            pages = pages.Where(page =>
                (page.ModifiedDate ?? page.CreatedDate) >= start ||
                repository.SiteBuilderSharedComponentReferences
                    .Where(reference => reference.PageId == page.Id)
                    .Join(
                        repository.SiteBuilderSharedComponents,
                        reference => reference.SharedComponentId,
                        component => component.Id,
                        (_, component) => component.ModifiedDate ?? component.CreatedDate)
                    .Any(changeDate => changeDate >= start));
        }

        if (modifiedBefore.HasValue)
        {
            var end = modifiedBefore.Value;
            pages = pages.Where(page =>
                (page.ModifiedDate ?? page.CreatedDate) <= end &&
                !repository.SiteBuilderSharedComponentReferences
                    .Where(reference => reference.PageId == page.Id)
                    .Join(
                        repository.SiteBuilderSharedComponents,
                        reference => reference.SharedComponentId,
                        component => component.Id,
                        (_, component) => component.ModifiedDate ?? component.CreatedDate)
                    .Any(changeDate => changeDate > end));
        }

        return pages;
    }

    protected override IList<SortInfo> BuildSortExpression(SiteBuilderPageSearchCriteria criteria)
    {
        var sortInfos = criteria.SortInfos;

        if (sortInfos.IsNullOrEmpty())
        {
            sortInfos =
            [
                new SortInfo { SortColumn = nameof(SiteBuilderPageEntity.CreatedDate), SortDirection = SortDirection.Descending },
                new SortInfo { SortColumn = nameof(SiteBuilderPageEntity.Id) },
            ];
        }

        return sortInfos;
    }
}
