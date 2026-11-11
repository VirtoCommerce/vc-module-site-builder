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

public class SiteBuilderSharedComponentSearchService(
    Func<ISiteBuilderRepository> repositoryFactory,
    IPlatformMemoryCache platformMemoryCache,
    ISiteBuilderSharedComponentService crudService,
    IOptions<CrudOptions> crudOptions)
    : SearchService<SiteBuilderSharedComponentSearchCriteria, SiteBuilderSharedComponentSearchResult,
        SiteBuilderSharedComponent, SiteBuilderSharedComponentEntity>(
        repositoryFactory, platformMemoryCache, crudService, crudOptions),
      ISiteBuilderSharedComponentSearchService
{
    protected override IQueryable<SiteBuilderSharedComponentEntity> BuildQuery(
        IRepository repository,
        SiteBuilderSharedComponentSearchCriteria criteria)
    {
        var query = ((ISiteBuilderRepository)repository).SiteBuilderSharedComponents;

        if (!string.IsNullOrWhiteSpace(criteria.StoreId))
        {
            query = query.Where(x => x.StoreId == criteria.StoreId);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Keyword))
        {
            query = query.Where(x => x.Name.Contains(criteria.Keyword));
        }

        if (!criteria.ObjectIds.IsNullOrEmpty())
        {
            query = query.Where(x => criteria.ObjectIds.Contains(x.Id));
        }

        return query;
    }

    protected override IList<SortInfo> BuildSortExpression(SiteBuilderSharedComponentSearchCriteria criteria)
    {
        return criteria.SortInfos.IsNullOrEmpty()
            ?
            [
                new SortInfo { SortColumn = nameof(SiteBuilderSharedComponentEntity.Name) },
                new SortInfo { SortColumn = nameof(SiteBuilderSharedComponentEntity.Id) },
            ]
            : criteria.SortInfos;
    }
}
