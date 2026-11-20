using Newtonsoft.Json;

namespace VirtoCommerce.SiteBuilder.Core.History;

public class ContentVersionPage
{
    public ContentHistoryStatus Status { get; set; }

    public IList<ContentVersion> Versions { get; set; } = [];

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string Cursor { get; set; }
}
