namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>The templates of a store's current theme to read for sections of a type.</summary>
public class SiteBuilderTemplateSectionScanCriteria
{
    public string StoreId { get; set; }

    /// <summary>The published templates; otherwise the drafts, a template without a draft read as published.</summary>
    public bool Published { get; set; }

    public string SectionType { get; set; }

    public SiteBuilderSectionSettingCondition Condition { get; set; }
}
