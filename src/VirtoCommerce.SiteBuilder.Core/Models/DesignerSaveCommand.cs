namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerSaveCommand
{
    public string StoreId { get; set; }

    public string SaveId { get; set; }

    /// <summary>The caller has already checked access to it.</summary>
    public GroupedSiteBuilderPage Group { get; set; }

    public string PageContent { get; set; }

    public string PageETag { get; set; }

    public SiteBuilderContentFile FilePage { get; set; }

    /// <summary>The version the draft of <see cref="FilePage"/> has to be at; <c>null</c> saves over whatever it holds.</summary>
    public string FilePageVersion { get; set; }

    public IList<DesignerSaveFile> ThemeFiles { get; set; } = [];

    /// <summary>The section schema catalog the designer previewed; without it the content is checked without a schema.</summary>
    public string CatalogHash { get; set; }
}
