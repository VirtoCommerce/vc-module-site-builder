namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageLinkOperationRequest : MultilanguagePageOperationRequest
{
    /// <summary>The page that becomes a language version.</summary>
    public string GroupId { get; set; }
}
