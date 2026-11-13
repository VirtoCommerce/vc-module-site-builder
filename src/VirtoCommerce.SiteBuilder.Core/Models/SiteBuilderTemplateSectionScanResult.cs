namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>Every template of the theme is read; those that are not template documents are listed as unread.</summary>
public class SiteBuilderTemplateSectionScanResult
{
    public string ThemeName { get; set; }

    public int TemplateCount { get; set; }

    /// <summary>Templates with at least one matching section.</summary>
    public IList<SiteBuilderTemplateSectionScanTemplate> Templates { get; set; } = [];

    public IList<string> UnreadTemplateKeys { get; set; } = [];
}

public class SiteBuilderTemplateSectionScanTemplate
{
    /// <summary>"product", "category.bikes".</summary>
    public string Key { get; set; }

    /// <summary>The path in the theme: "templates/product.json".</summary>
    public string Path { get; set; }

    /// <summary>The template has a draft, and the draft was read.</summary>
    public bool IsDraft { get; set; }

    public IList<SiteBuilderSectionMatch> Sections { get; set; } = [];
}
