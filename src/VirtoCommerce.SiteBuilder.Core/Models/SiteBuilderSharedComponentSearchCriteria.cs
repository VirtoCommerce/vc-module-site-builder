using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderSharedComponentSearchCriteria : SearchCriteriaBase, IHasStoreId
{
    public string StoreId { get; set; }
}
