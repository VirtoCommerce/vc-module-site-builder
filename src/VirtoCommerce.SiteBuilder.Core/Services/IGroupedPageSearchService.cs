using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.GenericCrud;

namespace VirtoCommerce.SiteBuilder.Core.Services
{
    public interface IGroupedPageSearchService : ISearchService<SiteBuilderPageSearchCriteria, GroupedSiteBuilderPageSearchResult, GroupedSiteBuilderPage>
    {
    }
}
