namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageLinkOperationRequest : MultilanguagePageOperationRequest
{
    /// <summary>The page that becomes a language version.</summary>
    public string GroupId { get; set; }

    /// <summary>The link is refused when the page is published, as read under the lock of the write.</summary>
    public bool RequireUnpublished { get; set; }
}
