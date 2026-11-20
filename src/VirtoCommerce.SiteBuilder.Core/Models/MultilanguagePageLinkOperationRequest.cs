namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageLinkOperationRequest : MultilanguagePageOperationRequest
{
    /// <summary>The page that becomes a language version.</summary>
    public string GroupId { get; set; }

    /// <summary>The link is refused when the page is published, as read under the lock of the write.</summary>
    public bool RequireUnpublished { get; set; }

    /// <summary>The multilanguage page's version the caller reviewed; <c>null</c> skips the check.</summary>
    public string ExpectedMultilanguagePageVersion { get; set; }

    /// <summary>The page's settings version the caller reviewed, compared under the lock of the write; <c>null</c> skips the check.</summary>
    public string ExpectedSettingsVersion { get; set; }
}
