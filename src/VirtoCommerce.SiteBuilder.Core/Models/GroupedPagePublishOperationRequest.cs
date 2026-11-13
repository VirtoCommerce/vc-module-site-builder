namespace VirtoCommerce.SiteBuilder.Core.Models;

public class GroupedPagePublishOperationRequest : GroupedPageOperationRequest
{
    /// <summary><c>true</c> publishes the draft; <c>false</c> turns the published page back into a draft.</summary>
    public bool Publish { get; set; }

    public string SaveId { get; set; }
}
