using Microsoft.Extensions.Options;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.GenericCrud;
using VirtoCommerce.Platform.Data.GenericCrud;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Data.Services
{
    public class GroupedPageSearchService(
        Func<ISiteBuilderRepository> repositoryFactory,
        IPlatformMemoryCache platformMemoryCache,
        IGroupedPageService crudService,
        IOptions<CrudOptions> crudOptions)
        : SearchService<SiteBuilderPageSearchCriteria, GroupedSiteBuilderPageSearchResult, GroupedSiteBuilderPage,
                GroupedSiteBuilderPageEntity>(repositoryFactory, platformMemoryCache, crudService, crudOptions),
            IGroupedPageSearchService
    {
        /// <summary>
        /// The filtered grouped pages query, reused by the pages list so both apply the same filters.
        /// </summary>
        public virtual IQueryable<GroupedSiteBuilderPageEntity> GetFilteredQuery(ISiteBuilderRepository repository, SiteBuilderPageSearchCriteria criteria)
        {
            return BuildQuery(repository, criteria);
        }

        protected override IQueryable<GroupedSiteBuilderPageEntity> BuildQuery(IRepository repository, SiteBuilderPageSearchCriteria criteria)
        {
            var query = ((ISiteBuilderRepository)repository).GroupedSiteBuilderPages;

            if (!string.IsNullOrEmpty(criteria.StoreId))
            {
                query = query.Where(x => x.StoreId == criteria.StoreId);
            }

            if (criteria.StoreIds != null)
            {
                var storeIds = criteria.StoreIds;
                query = query.Where(x => storeIds.Contains(x.StoreId));
            }

            if (!criteria.Keyword.IsNullOrEmpty())
            {
                // PostgreSQL compares case-sensitively; lower case on both sides ignores case on every provider.
                var keyword = criteria.Keyword.ToLowerInvariant();
#pragma warning disable CA1862 // IQueryable: the StringComparison overload does not translate to SQL
                query = query.Where(x => x.Name.ToLower().Contains(keyword) || x.Permalink.ToLower().Contains(keyword));
#pragma warning restore CA1862
            }

            query = ApplyStatusFilter(query, criteria);
            query = ApplyActiveOnFilter(query, criteria);
            query = ApplyLifecycleFilter(query, criteria);

            if (!string.IsNullOrEmpty(criteria.LanguageCode))
            {
                query = query.Where(x => x.CultureName == criteria.LanguageCode);
            }

            if (!string.IsNullOrEmpty(criteria.MultilanguagePageId))
            {
                query = query.Where(x => x.MultilanguagePageId == criteria.MultilanguagePageId);
            }

            return query;
        }

        protected virtual IQueryable<GroupedSiteBuilderPageEntity> ApplyStatusFilter(IQueryable<GroupedSiteBuilderPageEntity> query, SiteBuilderPageSearchCriteria criteria)
        {
            if (!string.IsNullOrEmpty(criteria.Statuses))
            {
                var statuses = criteria.Statuses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (statuses.Contains(Archived))
                {
                    // group is archived when all pages are archived
                    // so, we need to split the statuses and check if there are other statuses except "archived"
                    var withoutArchive = statuses.Where(x => x != Archived).ToArray();
                    query = query
                        .Where(x =>
                            x.Pages.All(p => p.Status == Archived) ||
                            x.Pages.Any(p => withoutArchive.Contains(p.Status))
                        );
                }
                else
                {
                    query = query.Where(x => x.Pages.Any(p => statuses.Contains(p.Status)));
                }
            }

            return query;
        }

        protected virtual IQueryable<GroupedSiteBuilderPageEntity> ApplyActiveOnFilter(IQueryable<GroupedSiteBuilderPageEntity> query, SiteBuilderPageSearchCriteria criteria)
        {
            if (criteria.ActiveOn.HasValue && criteria.Lifecycle.IsNullOrEmpty())
            {
                var date = criteria.ActiveOn.Value;
                query = query.Where(x =>
                    (x.StartDate == null || x.StartDate <= date) &&
                    (x.EndDate == null || x.EndDate >= date));
            }

            return query;
        }

        protected virtual IQueryable<GroupedSiteBuilderPageEntity> ApplyLifecycleFilter(IQueryable<GroupedSiteBuilderPageEntity> query, SiteBuilderPageSearchCriteria criteria)
        {
            if (!criteria.Lifecycle.IsNullOrEmpty())
            {
                query = ApplyLifecycleQuery(query, criteria);
            }

            if (criteria.ObjectIds is { Count: > 0 })
            {
                query = query.Where(x => criteria.ObjectIds.Contains(x.Id));
            }

            return query;
        }

        private static IQueryable<GroupedSiteBuilderPageEntity> ApplyLifecycleQuery(IQueryable<GroupedSiteBuilderPageEntity> query, SiteBuilderPageSearchCriteria criteria)
        {
            var now = criteria.ActiveOn ?? DateTime.UtcNow;

            return criteria.Lifecycle switch
            {
                PageLifecycleFilters.Drafts => query.Where(g => g.Pages.Any(p => p.Status == Draft)),
                PageLifecycleFilters.Pending => query.Where(g =>
                    g.Pages.Any(p => p.Status == Published) &&
                    g.StartDate != null &&
                    g.StartDate > now),
                PageLifecycleFilters.Active => query
                    .Where(g => g.Pages.Any(p => p.Status == Published))
                    .Where(g => (g.StartDate == null || g.StartDate <= now) &&
                                (g.EndDate == null || g.EndDate >= now)),
                PageLifecycleFilters.Archived => query.Where(g =>
                    g.Pages.All(p => p.Status == Archived) ||
                    (g.EndDate != null && g.EndDate < now)),
                _ => query,
            };
        }



        protected override IList<SortInfo> BuildSortExpression(SiteBuilderPageSearchCriteria criteria)
        {
            var sortInfos = criteria.SortInfos;

            if (sortInfos.IsNullOrEmpty())
            {
                sortInfos =
                [
                    new SortInfo { SortColumn = nameof(GroupedSiteBuilderPageEntity.CreatedDate), SortDirection = SortDirection.Descending },
                    new SortInfo { SortColumn = nameof(GroupedSiteBuilderPageEntity.Id) },
                ];
            }

            return sortInfos;
        }
    }
}
