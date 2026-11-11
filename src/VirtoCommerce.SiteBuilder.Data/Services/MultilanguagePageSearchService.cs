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

public class MultilanguagePageSearchService(
    Func<ISiteBuilderRepository> repositoryFactory,
    IPlatformMemoryCache platformMemoryCache,
    IMultilanguagePageService crudService,
    IOptions<CrudOptions> crudOptions)
    : SearchService<MultilanguagePageSearchCriteria, MultilanguagePageSearchResult, MultilanguagePage, MultilanguagePageEntity>(
        repositoryFactory, platformMemoryCache, crudService, crudOptions),
      IMultilanguagePageSearchService
{
    protected override IQueryable<MultilanguagePageEntity> BuildQuery(IRepository repository, MultilanguagePageSearchCriteria criteria)
    {
        var query = ((ISiteBuilderRepository)repository).MultilanguagePages;

        if (!string.IsNullOrWhiteSpace(criteria.StoreId))
        {
            query = query.Where(x => x.StoreId == criteria.StoreId);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Keyword))
        {
            // Ignores case on every provider, like the page search (see GroupedPageSearchService).
            var keyword = criteria.Keyword.ToLowerInvariant();
#pragma warning disable CA1862 // IQueryable: the StringComparison overload does not translate to SQL
            query = query.Where(x => x.Name.ToLower().Contains(keyword) || x.Permalink.ToLower().Contains(keyword));
#pragma warning restore CA1862
        }

        if (!criteria.ObjectIds.IsNullOrEmpty())
        {
            query = query.Where(x => criteria.ObjectIds.Contains(x.Id));
        }

        return query;
    }

    protected override IList<SortInfo> BuildSortExpression(MultilanguagePageSearchCriteria criteria)
    {
        return criteria.SortInfos.IsNullOrEmpty()
            ?
            [
                new SortInfo { SortColumn = nameof(MultilanguagePageEntity.Name) },
                new SortInfo { SortColumn = nameof(MultilanguagePageEntity.Id) },
            ]
            : criteria.SortInfos;
    }
}
