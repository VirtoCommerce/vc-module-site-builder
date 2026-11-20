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

    /// <summary>The version the page's draft has to be at; <c>null</c> discards it whatever it holds.</summary>
    public string PageETag { get; set; }

    /// <summary>The version each theme file draft has to be at, by its path; <c>null</c> discards them whatever they hold.</summary>
    public IDictionary<string, string> ThemeFileETags { get; set; }
}
