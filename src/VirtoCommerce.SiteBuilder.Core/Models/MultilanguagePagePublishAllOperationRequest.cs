namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePagePublishAllOperationRequest : MultilanguagePageOperationRequest
{
    /// <summary>
    /// The version of every draft the caller reviewed, by the id of its language version, as a dry run answers them in
    /// <see cref="SiteBuilderObjectChange.ObjectId"/> and <see cref="SiteBuilderObjectChange.ETag"/>. Nothing is published when the drafts are not
    /// those any more: one changed, appeared or went. <c>null</c> publishes the drafts whatever they hold.
    /// </summary>
    public IDictionary<string, string> ExpectedDraftVersions { get; set; }
}
