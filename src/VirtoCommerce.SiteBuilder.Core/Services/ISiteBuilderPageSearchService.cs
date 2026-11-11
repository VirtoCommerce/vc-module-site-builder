using VirtoCommerce.Platform.Core.GenericCrud;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderPageSearchService : ISearchService<SiteBuilderPageSearchCriteria, SiteBuilderPageSearchResult, SiteBuilderPage>
{
}
