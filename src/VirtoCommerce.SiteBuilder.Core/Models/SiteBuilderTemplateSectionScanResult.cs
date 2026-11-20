namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderTemplateSectionScanResult
{
    public string ThemeName { get; set; }

    public int TemplateCount { get; set; }

    public IList<SiteBuilderTemplateSectionScanTemplate> Templates { get; set; } = [];

    public IList<string> UnreadTemplateKeys { get; set; } = [];
}

public class SiteBuilderTemplateSectionScanTemplate
{
    public string Key { get; set; }

    public string Path { get; set; }

    public bool IsDraft { get; set; }

    public IList<SiteBuilderSectionMatch> Sections { get; set; } = [];
}
