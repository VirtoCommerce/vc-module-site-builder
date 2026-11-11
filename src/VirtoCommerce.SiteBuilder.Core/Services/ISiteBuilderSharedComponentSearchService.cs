using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.GenericCrud;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderSharedComponentSearchService
    : ISearchService<SiteBuilderSharedComponentSearchCriteria, SiteBuilderSharedComponentSearchResult, SiteBuilderSharedComponent>;
