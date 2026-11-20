namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderStoreRequest : IHasStoreId
{
    public string StoreId { get; set; }

    public string CatalogHash { get; set; }
}
