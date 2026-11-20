using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SharedComponentContentSaveOperationRequest : SharedComponentOperationRequest
{
    public JObject Content { get; set; }

    /// <summary>The <see cref="SiteBuilderContentVersion.CreateForFileContent"/> version the caller reviewed; <c>null</c> skips the check.</summary>
    public string ExpectedContentVersion { get; set; }
}
