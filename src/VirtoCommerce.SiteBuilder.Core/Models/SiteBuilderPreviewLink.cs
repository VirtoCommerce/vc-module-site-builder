namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>Where the storefront shows a page or a template.</summary>
public class SiteBuilderPreviewLink
{
    public string Url { get; set; }

    /// <summary>The commit of a page kept in git the link shows; the link keeps showing it, as a commit does not change.</summary>
    public string Ref { get; set; }
}
