namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>
/// What a scan read and what it found. "Not found" holds only for a complete scan: pages after the scanned rows, and
/// versions or Shared Components the scan could not read, were not looked at.
/// </summary>
public class SiteBuilderSectionScanResult
{
    /// <summary>Rows of the pages list the filter matches.</summary>
    public int TotalPageCount { get; set; }

    /// <summary>The first scanned row.</summary>
    public int Skip { get; set; }

    public int ScannedPageCount { get; set; }

    /// <summary>Language versions read, a Landing Page counting as one.</summary>
    public int ScannedVersionCount { get; set; }

    /// <summary>Every row of the filter was scanned in this call.</summary>
    public bool IsComplete => Skip == 0 && ScannedPageCount >= TotalPageCount;

    /// <summary>The Skip that scans the next rows; null after the last row.</summary>
    public int? NextSkip => Skip + ScannedPageCount < TotalPageCount ? Skip + ScannedPageCount : null;

    /// <summary>Language versions with at least one matching section.</summary>
    public IList<SiteBuilderSectionScanPage> Pages { get; set; } = [];

    /// <summary>Language versions that were in the scanned rows but could not be read.</summary>
    public IList<SiteBuilderSectionScanSkip> Unread { get; set; } = [];
}

public class SiteBuilderSectionScanPage
{
    /// <summary>The language version (or Landing Page) the sections are in.</summary>
    public GroupedSiteBuilderPage Page { get; set; }

    /// <summary>The row of the pages list: the Landing Page, or the Multilanguage Page of the language version.</summary>
    public SiteBuilderPageListItem ListItem { get; set; }

    /// <summary>The page version whose content was read.</summary>
    public string ContentPageId { get; set; }

    /// <summary>The version token of the content read, as the designer's save expects it.</summary>
    public string ETag { get; set; }

    public bool IsLegacyShape { get; set; }

    public IList<SiteBuilderSectionMatch> Sections { get; set; } = [];

    /// <summary>Shared Components placed in the page whose sections were not read.</summary>
    public IList<string> UnreadSharedComponentIds { get; set; } = [];
}

public class SiteBuilderSectionMatch
{
    /// <summary>
    /// The id of the section in the page; for a section of a placed Shared Component, its id in the component.
    /// Sections stored without an id get one from their place, as the designer and the MCP tools name them.
    /// </summary>
    public string SectionId { get; set; }

    public string SectionType { get; set; }

    /// <summary>The 0-based place of the section, or of the placement, among the page's sections.</summary>
    public int Position { get; set; }

    public bool Hidden { get; set; }

    /// <summary>Set when the section comes from a placed Shared Component: a change of the page does not change it.</summary>
    public string SharedComponentId { get; set; }

    public string PlacementId { get; set; }
}

public class SiteBuilderSectionScanSkip
{
    public const string Deleted = "Deleted";
    public const string NoContent = "NoContent";
    public const string NotPublished = "NotPublished";
    public const string NotADocument = "NotADocument";

    public string PageId { get; set; }

    public string StoreId { get; set; }

    public string CultureName { get; set; }

    /// <summary><see cref="Deleted"/>, <see cref="NoContent"/>, <see cref="NotPublished"/> or <see cref="NotADocument"/>.</summary>
    public string Reason { get; set; }
}
