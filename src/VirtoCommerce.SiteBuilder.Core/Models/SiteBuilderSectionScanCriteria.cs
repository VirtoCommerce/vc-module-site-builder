using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderSectionScanCriteria
{
    /// <summary>
    /// Selects rows of the pages list, a Multilanguage Page being one row; only the language, statuses and object ids also narrow its language
    /// versions. <see cref="SearchCriteriaBase.Skip"/> is the first row; <see cref="MaxPages"/> replaces Take.
    /// </summary>
    public SiteBuilderPageSearchCriteria Pages { get; set; } = new();

    public int MaxPages { get; set; }

    /// <summary><c>false</c> reads the draft, or the published version of a page with no draft.</summary>
    public bool Published { get; set; }

    public string SectionType { get; set; }

    public SiteBuilderSectionSettingCondition Condition { get; set; }

    /// <summary>Stores whose Shared Components the scan opens; <c>null</c> opens all. A placement from any other store is reported unread.</summary>
    public IList<string> SharedComponentStoreIds { get; set; }
}
