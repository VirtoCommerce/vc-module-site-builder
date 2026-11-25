namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A change of whether a page group is a language version: a convert or an unlink.</summary>
public class MultilanguagePageVersionOperationRequest : GroupedPageOperationRequest
{
    /// <summary>The change is refused when the page is published, as read under the lock of its write.</summary>
    public bool RequireUnpublished { get; set; }

    /// <summary>The page's settings version the caller reviewed, compared under the lock of the write; <c>null</c> skips the check.</summary>
    public string ExpectedSettingsVersion { get; set; }
}
