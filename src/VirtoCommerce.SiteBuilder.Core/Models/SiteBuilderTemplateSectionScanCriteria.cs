namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderTemplateSectionScanCriteria
{
    public string StoreId { get; set; }

    /// <summary><c>false</c> reads the drafts, or the published template when there is no draft.</summary>
    public bool Published { get; set; }

    public string SectionType { get; set; }

    public SiteBuilderSectionSettingCondition Condition { get; set; }
}
