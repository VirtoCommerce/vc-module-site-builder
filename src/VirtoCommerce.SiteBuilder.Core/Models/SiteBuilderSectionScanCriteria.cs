using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>
/// Which database pages to read for sections of a type. There is no index of section types: the scan reads the content
/// of every page it covers, so it reads at most <see cref="MaxPages"/> rows of the pages list per call.
/// </summary>
public class SiteBuilderSectionScanCriteria
{
    /// <summary>
    /// The pages, as the pages list filters them (stores, statuses, language, page type, keyword, ids). A Multilanguage
    /// Page is one row; its language versions are scanned one by one, those of <see cref="SearchCriteriaBase.LanguageCode"/>
    /// and of the statuses only when they are set. Skip is the first row to scan; Take is not used.
    /// </summary>
    public SiteBuilderPageSearchCriteria Pages { get; set; } = new();

    /// <summary>Rows of the pages list to read, from Skip.</summary>
    public int MaxPages { get; set; }

    /// <summary>
    /// The published version, what the storefront shows; otherwise the draft, which is the published version when the
    /// page has no unpublished changes. Chosen before the sections are filtered.
    /// </summary>
    public bool Published { get; set; }

    public string SectionType { get; set; }

    public SiteBuilderSectionSettingCondition Condition { get; set; }

    /// <summary>
    /// Stores whose Shared Components the scan opens, to find the sections a placed component brings into a page;
    /// null opens those of every store. A placement of a component of any other store is reported unread.
    /// </summary>
    public IList<string> SharedComponentStoreIds { get; set; }
}
