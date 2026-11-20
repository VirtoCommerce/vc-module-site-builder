using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderContentFile
{
    public string ContentType { get; set; }

    public string Path { get; set; }

    public JToken Content { get; set; }
}
