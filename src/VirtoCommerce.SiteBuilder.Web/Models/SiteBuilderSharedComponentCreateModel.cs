using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Web.Models;

public class SiteBuilderSharedComponentCreateModel
{
    public string StoreId { get; set; }

    public string Name { get; set; }

    public JObject Content { get; set; }
}
