namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>"Not found" holds only when <see cref="IsComplete"/> and nothing is unread.</summary>
public class SiteBuilderSectionScanResult
{
    public int TotalPageCount { get; set; }

    public int Skip { get; set; }

    public int ScannedPageCount { get; set; }

    public int ScannedVersionCount { get; set; }

    public bool IsComplete => Skip == 0 && ScannedPageCount >= TotalPageCount;

    public int? NextSkip => Skip + ScannedPageCount < TotalPageCount ? Skip + ScannedPageCount : null;

    public IList<SiteBuilderSectionScanPage> Pages { get; set; } = [];

    public IList<SiteBuilderSectionScanUnread> Unread { get; set; } = [];
}

public class SiteBuilderSectionScanPage
{
    public GroupedSiteBuilderPage Page { get; set; }

    public SiteBuilderPageListItem ListItem { get; set; }

    public string ContentPageId { get; set; }

    public string ETag { get; set; }

    public bool IsLegacyShape { get; set; }

    public IList<SiteBuilderSectionMatch> Sections { get; set; } = [];

    public IList<string> UnreadSharedComponentIds { get; set; } = [];
}

public class SiteBuilderSectionMatch
{
    public string SectionId { get; set; }

    public string SectionType { get; set; }

    public int Position { get; set; }

    public bool Hidden { get; set; }

    public string SharedComponentId { get; set; }

    public string PlacementId { get; set; }
}

public class SiteBuilderSectionScanUnread
{
    public const string Deleted = "Deleted";
    public const string NoContent = "NoContent";
    public const string NotPublished = "NotPublished";
    public const string NotADocument = "NotADocument";

    public string PageId { get; set; }

    public string StoreId { get; set; }

    public string CultureName { get; set; }

    public string Reason { get; set; }
}
