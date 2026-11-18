namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageLinkOperationRequest : MultilanguagePageOperationRequest
{
    /// <summary>The page that becomes a language version.</summary>
    public string GroupId { get; set; }

    /// <summary>The link is refused when the page is published, as read under the lock of the write.</summary>
    public bool RequireUnpublished { get; set; }

    /// <summary>
    /// The version of the multilanguage page the caller reviewed, as a dry run tells it in <see cref="SiteBuilderObjectChange.ETag"/>: the link
    /// is refused once someone changed it. <c>null</c> links to whatever it holds.
    /// </summary>
    public string ExpectedMultilanguagePageVersion { get; set; }

    /// <summary>
    /// The version of the page's settings the caller reviewed, as a dry run tells it in <see cref="SiteBuilderObjectChange.ETag"/>: the link is
    /// refused once someone changed them, as read under the lock of the write. <c>null</c> links the page whatever it holds.
    /// </summary>
    public string ExpectedSettingsVersion { get; set; }
}
