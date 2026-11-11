using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageSearchCriteria : SearchCriteriaBase, IHasStoreId
{
    public string StoreId { get; set; }
}
