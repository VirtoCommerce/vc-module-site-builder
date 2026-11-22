namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerStorefront
{
    public string Url { get; set; }

    public string PreviewPath { get; set; }

    /// <summary>The asset library's public base, as the snapshot gives it: the designer's draft carries it for a store never published.</summary>
    public string AssetsBaseUrl { get; set; }
}
