namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A change of whether a page group is a language version: a convert or an unlink.</summary>
public class MultilanguagePageVersionOperationRequest : GroupedPageOperationRequest
{
    /// <summary>The change is refused when the page is published, as read under the lock of its write.</summary>
    public bool RequireUnpublished { get; set; }
}
