namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A page or a blog article kept as a file.</summary>
public class SiteBuilderFilePageQueryRequest : SiteBuilderOperationRequest
{
    /// <summary>pages or blogs; <c>null</c> for pages.</summary>
    public string ContentType { get; set; }

    public string Path { get; set; }
}
