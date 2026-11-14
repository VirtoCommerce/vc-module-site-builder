using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SharedComponentContentSaveOperationRequest : SharedComponentOperationRequest
{
    public JObject Content { get; set; }
}
