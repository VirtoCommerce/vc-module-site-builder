namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderFilePagePreviewRequest : SiteBuilderFilePageQueryRequest
{
    /// <summary>The version of the page in git, by its commit; <c>null</c> for the user's draft, else what is published.</summary>
    public string Ref { get; set; }
}
