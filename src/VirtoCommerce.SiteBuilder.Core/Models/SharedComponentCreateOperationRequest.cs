using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SharedComponentCreateOperationRequest
{
    public string StoreId { get; set; }

    public string Name { get; set; }

    public JObject Content { get; set; }

    /// <summary>Checks the write as it would run and answers what it would create instead of creating it.</summary>
    public bool DryRun { get; set; }
}
