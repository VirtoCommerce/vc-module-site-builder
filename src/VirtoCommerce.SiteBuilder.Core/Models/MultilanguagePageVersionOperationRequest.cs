namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A change of whether a page group is a language version: a convert or an unlink.</summary>
public class MultilanguagePageVersionOperationRequest : GroupedPageOperationRequest
{
    /// <summary>The change is refused when the page is published, as read under the lock of its write.</summary>
    public bool RequireUnpublished { get; set; }

    /// <summary>
    /// The version of the page's settings the caller reviewed, as a dry run tells it in <see cref="SiteBuilderObjectChange.ETag"/>: the change
    /// is refused once someone changed them, as read under the lock of its write. <c>null</c> changes the page whatever it holds.
    /// </summary>
    public string ExpectedSettingsVersion { get; set; }
}
