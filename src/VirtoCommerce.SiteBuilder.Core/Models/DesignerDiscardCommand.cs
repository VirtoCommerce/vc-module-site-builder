namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerDiscardCommand
{
    public string StoreId { get; set; }

    public string SaveId { get; set; }

    /// <summary>The caller has already checked access to it.</summary>
    public GroupedSiteBuilderPage Group { get; set; }

    public SiteBuilderContentFile FilePage { get; set; }

    /// <summary>The theme files by their path with the theme folder.</summary>
    public IList<string> ThemeFiles { get; set; } = [];
}
