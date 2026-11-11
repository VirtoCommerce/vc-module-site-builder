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

    public IList<DesignerSaveFile> ThemeFiles { get; set; } = [];
}
