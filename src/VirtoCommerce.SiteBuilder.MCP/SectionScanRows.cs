using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.MCP;

// What search_site_pages answers with section_type: the pages among the scanned rows that hold a matching section, and
// how much was scanned. total_count counts the pages the page filters match, not the pages with the section.
public sealed class SitePageSectionSearch
{
    public IList<SitePageSectionRow> Items { get; init; } = [];

    // Pages the page filters match.
    public int TotalCount { get; init; }

    // The first scanned page, from 0.
    public int Skip { get; init; }

    public int ScannedCount { get; init; }

    public int ScannedLanguageVersions { get; init; }

    // Every page of the filters was scanned in this call: no item means no page has the section.
    public bool IsComplete { get; init; }

    // The skip of the next call, which scans the next pages; null when the last page was scanned.
    public int? NextSkip { get; init; }

    public bool HasMore => NextSkip != null;

    public string Coverage { get; init; }

    // Language versions in the scanned pages that could not be read.
    public IList<SectionScanUnreadRow> Unread { get; init; } = [];

    public static SitePageSectionSearch From(SiteBuilderSectionScanResult scan)
    {
        var items = scan.Pages
            .GroupBy(x => x.ListItem.Id, StringComparer.Ordinal)
            .Select(x => SitePageSectionRow.From(x.First().ListItem, x.ToList()))
            .ToList();

        return new SitePageSectionSearch
        {
            Items = items,
            TotalCount = scan.TotalPageCount,
            Skip = scan.Skip,
            ScannedCount = scan.ScannedPageCount,
            ScannedLanguageVersions = scan.ScannedVersionCount,
            IsComplete = scan.IsComplete,
            NextSkip = scan.NextSkip,
            Coverage = SectionScanCoverage.Describe(scan),
            Unread = scan.Unread.Select(SectionScanUnreadRow.From).ToList(),
        };
    }

    public static SitePageSectionSearch Empty() => new() { IsComplete = true, Coverage = SectionScanCoverage.NoPages };
}

public sealed class SitePageSectionRow
{
    // The page id of a Landing Page; the Multilanguage Page id of a Multilanguage Page.
    public string Id { get; init; }

    public string Type { get; init; }

    public string Name { get; init; }

    public string Permalink { get; init; }

    public string StoreId { get; init; }

    // One per language version that holds a matching section.
    public IList<SectionVersionMatchRow> Matches { get; init; } = [];

    public static SitePageSectionRow From(SiteBuilderPageListItem row, IList<SiteBuilderSectionScanPage> versions)
    {
        return new SitePageSectionRow
        {
            Id = row.Id,
            Type = row.Type,
            Name = row.Name,
            Permalink = row.Permalink,
            StoreId = versions[0].Page.StoreId,
            Matches = versions.Select(SectionVersionMatchRow.From).ToList(),
        };
    }
}

public sealed class SectionVersionMatchRow
{
    // The page id to pass to edit_page_draft: the Landing Page, or the language version.
    public string PageId { get; init; }

    public string Language { get; init; }

    // The version token of the content scanned, as edit_page_draft takes it.
    public string VersionToken { get; init; }

    public IList<SectionMatchRow> Sections { get; init; } = [];

    // Shared Components placed on the page whose sections were not read.
    public IList<string> UnreadSharedComponentIds { get; init; } = [];

    public static SectionVersionMatchRow From(SiteBuilderSectionScanPage page) => new()
    {
        PageId = page.Page.Id,
        Language = page.Page.CultureName,
        VersionToken = page.ETag,
        Sections = page.Sections.Select(SectionMatchRow.From).ToList(),
        UnreadSharedComponentIds = page.UnreadSharedComponentIds,
    };
}

public sealed class SectionMatchRow
{
    public string Id { get; init; }

    public string Type { get; init; }

    public int Position { get; init; }

    public bool Hidden { get; init; }

    // Set when the section comes from a placed Shared Component: edit the component, not the page.
    public string SharedComponentId { get; init; }

    public string PlacementId { get; init; }

    public static SectionMatchRow From(SiteBuilderSectionMatch match) => new()
    {
        Id = match.SectionId,
        Type = match.SectionType,
        Position = match.Position,
        Hidden = match.Hidden,
        SharedComponentId = match.SharedComponentId,
        PlacementId = match.PlacementId,
    };
}

public sealed class SectionScanUnreadRow
{
    public string PageId { get; init; }

    public string StoreId { get; init; }

    public string Language { get; init; }

    public string Reason { get; init; }

    public static SectionScanUnreadRow From(SiteBuilderSectionScanSkip skip) => new()
    {
        PageId = skip.PageId,
        StoreId = skip.StoreId,
        Language = skip.CultureName,
        Reason = skip.Reason switch
        {
            SiteBuilderSectionScanSkip.Deleted => "deleted while it was scanned",
            SiteBuilderSectionScanSkip.NotPublished => "has no published version",
            SiteBuilderSectionScanSkip.NoContent => "has no content in this version",
            _ => "its content is not a page document",
        },
    };
}

// How much of the filter a scan covered, in a sentence the agent can repeat: a scan never says "none" unless it read
// every page.
public static class SectionScanCoverage
{
    public const string NoPages = "No page matches the page filters, so there was nothing to scan.";

    public static string Describe(SiteBuilderSectionScanResult scan)
    {
        if (scan.TotalPageCount == 0)
        {
            return NoPages;
        }

        var first = scan.Skip + 1;
        var last = scan.Skip + scan.ScannedPageCount;
        var text = scan.IsComplete
            ? $"Scanned all {scan.TotalPageCount} pages ({scan.ScannedVersionCount} language versions)."
            : $"Scanned pages {first}-{last} of {scan.TotalPageCount} ({scan.ScannedVersionCount} language versions); " +
              (scan.NextSkip != null
                  ? $"pages {last + 1}-{scan.TotalPageCount} were not scanned, so the section may be on them too: call again with skip={scan.NextSkip}."
                  : $"pages 1-{scan.Skip} were scanned by earlier calls, not this one.");

        var unread = scan.Unread.Count(x => x.Reason != SiteBuilderSectionScanSkip.NotPublished);
        if (unread > 0)
        {
            text += $" {unread} language versions could not be read and are listed in unread.";
        }

        var unreadComponents = scan.Pages.Count(x => x.UnreadSharedComponentIds.Count > 0);
        if (unreadComponents > 0)
        {
            text += $" On {unreadComponents} language versions, placed Shared Components were not read (unread_shared_component_ids): " +
                "their sections need builder:shared-components:read in the page's store.";
        }

        return text;
    }
}
