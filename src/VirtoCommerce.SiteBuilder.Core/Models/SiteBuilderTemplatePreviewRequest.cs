namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderTemplatePreviewRequest : SiteBuilderThemeQueryRequest
{
    /// <summary>E.g. "product" or "product.with-video".</summary>
    public string TemplateKey { get; set; }
}
