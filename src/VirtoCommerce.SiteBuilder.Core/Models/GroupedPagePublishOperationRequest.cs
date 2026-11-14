namespace VirtoCommerce.SiteBuilder.Core.Models;

public class GroupedPagePublishOperationRequest : GroupedPageOperationRequest
{
    /// <summary><c>true</c> publishes the draft; <c>false</c> turns the published page back into a draft.</summary>
    public bool Publish { get; set; }

    public string SaveId { get; set; }

    /// <summary>
    /// The version of the draft the caller reviewed, as the content read and a dry run answer it in <see cref="SiteBuilderObjectChange.ETag"/>:
    /// the publish is refused once the draft is at another one. <c>null</c> publishes the draft whatever it holds.
    /// </summary>
    public string ExpectedDraftVersion { get; set; }

    /// <summary>
    /// The version of the published page the caller reviewed, as the content read and a dry run answer it in <see cref="SiteBuilderObjectChange.ETag"/>:
    /// the unpublish is refused once the page is at another one. <c>null</c> unpublishes the page whatever it holds.
    /// </summary>
    public string ExpectedPublishedVersion { get; set; }
}
