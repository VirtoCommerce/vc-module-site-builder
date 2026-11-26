using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

/// <summary>The pages that place a custom section type, as far as one bounded scan reads them.</summary>
public class SectionTypePages(
    ISiteBuilderSectionScanner sectionScanner,
    IAuthorizationService authorizationService,
    Func<UserManager<ApplicationUser>> userManagerFactory)
{
    public const int MaxPages = 50;

    private const string PublishedVersion = "published";

    private const string DraftVersion = "draft";

    private const string TemplatesNotScanned = " Templates are not scanned: search_templates lists them.";

    private const int MaxNamedPages = 10;

    /// <param name="published"><c>true</c> reads the published version of each page, <c>false</c> its draft, else its published version.</param>
    public async Task<(IList<PlacingPageRow> Pages, string Coverage)> FindAsync(ClaimsPrincipal user, string storeId, string type, bool published,
        CancellationToken cancellationToken)
    {
        var storeIds = await authorizationService.GetStoreIdsAsync(user, userManagerFactory, storeId, SharedComponentsRead);
        var scan = await ScanAsync(storeId, type, published, storeIds, cancellationToken);

        return (ToRows(scan.Pages, version: null).ToList(), SectionScanCoverage.Describe(scan) + TemplatesNotScanned);
    }

    /// <summary>
    /// The pages whose published version places the type and those whose draft does, a row per version, each marked by <see cref="PlacingPageRow.Version"/>.
    /// </summary>
    public async Task<(IList<PlacingPageRow> Pages, string Coverage)> FindPublishedAndDraftsAsync(ClaimsPrincipal user, string storeId, string type,
        CancellationToken cancellationToken)
    {
        var (pages, scan) = await ScanPublishedAndDraftsAsync(user, storeId, type, cancellationToken);

        return (pages, SectionScanCoverage.Describe(scan) + " Each page was read in its published version and in its draft, where it has one." +
            TemplatesNotScanned);
    }

    /// <summary>
    /// For a warning: how many pages place the type in their published version or in their draft, each page once, a Multilanguage Page with all its
    /// language versions included, which they are, and that others may place it too when the scan did not read every page.
    /// </summary>
    public async Task<string> DescribePublishedAndDraftsAsync(ClaimsPrincipal user, string storeId, string type, CancellationToken cancellationToken)
    {
        var (_, scan) = await ScanPublishedAndDraftsAsync(user, storeId, type, cancellationToken);

        return DescribePlacing(GetPlacingPages(scan)) + DescribeUnread(scan);
    }

    private static List<(string Id, string Name, string Permalink)> GetPlacingPages(SiteBuilderSectionScanResult scan) =>
        scan.Pages
            .Where(x => x.Sections.Count > 0)
            .Select(x => (Id: x.ListItem?.Id ?? x.Page.MultilanguagePageId ?? x.Page.Id, Name: CustomSectionText.Quote(x.ListItem?.Name ?? x.Page.Name),
                Permalink: x.ListItem?.Permalink ?? x.Page.Permalink))
            .DistinctBy(x => x.Id)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

    private static string DescribePlacing(List<(string Id, string Name, string Permalink)> placing)
    {
        var named = placing.Take(MaxNamedPages).Select(x => x.Permalink == null ? $"'{x.Name}'" : $"'{x.Name}' ({x.Permalink})").ToList();
        if (placing.Count > MaxNamedPages)
        {
            named.Add($"{placing.Count - MaxNamedPages} more");
        }

        return placing.Count switch
        {
            0 => "No page places it.",
            1 => $"1 page places it and will show nothing in its place: {named[0]}.",
            _ => $"{placing.Count} pages place it and will show nothing in its place: {CustomSectionText.JoinAnd(named)}.",
        };
    }

    private static string DescribeUnread(SiteBuilderSectionScanResult scan)
    {
        var text = string.Empty;
        if (!scan.IsComplete)
        {
            text += $" Only {scan.ScannedPageCount} of the store's {scan.TotalPageCount} pages were read, so others may place it too.";
        }

        var unread = scan.Unread.Where(x => x.Reason != SiteBuilderSectionScanUnread.NotPublished).Select(x => (x.PageId, x.CultureName)).Distinct().Count();
        text += unread switch
        {
            0 => string.Empty,
            1 => " 1 language version could not be read, so it may place it too.",
            _ => $" {unread} language versions could not be read, so they may place it too.",
        };

        return text;
    }

    private async Task<(IList<PlacingPageRow> Pages, SiteBuilderSectionScanResult Scan)> ScanPublishedAndDraftsAsync(ClaimsPrincipal user, string storeId,
        string type, CancellationToken cancellationToken)
    {
        var storeIds = await authorizationService.GetStoreIdsAsync(user, userManagerFactory, storeId, SharedComponentsRead);
        var published = await ScanAsync(storeId, type, published: true, storeIds, cancellationToken);
        var drafts = await ScanAsync(storeId, type, published: false, storeIds, cancellationToken);

        // A page with no draft answers the draft read with its published or archived version.
        var draftVersions = drafts.Pages.Where(IsDraftVersion).ToList();

        // The draft read covers every page, so its counts are what was read.
        var scan = new SiteBuilderSectionScanResult
        {
            TotalPageCount = drafts.TotalPageCount,
            Skip = drafts.Skip,
            ScannedPageCount = drafts.ScannedPageCount,
            ScannedVersionCount = drafts.ScannedVersionCount,
            Pages = [.. published.Pages, .. draftVersions],
            Unread = published.Unread.Concat(drafts.Unread).DistinctBy(x => (x.PageId, x.CultureName, x.Reason)).ToList(),
        };

        var pages = ToRows(published.Pages, PublishedVersion).Concat(ToRows(draftVersions, DraftVersion)).ToList();

        return (pages, scan);
    }

    private Task<SiteBuilderSectionScanResult> ScanAsync(string storeId, string type, bool published, IList<string> sharedComponentStoreIds,
        CancellationToken cancellationToken) =>
        sectionScanner.ScanPagesAsync(new SiteBuilderSectionScanCriteria
        {
            Pages = new SiteBuilderPageSearchCriteria { StoreId = storeId, Take = MaxPages },
            MaxPages = MaxPages,
            Published = published,
            SectionType = type,
            SharedComponentStoreIds = sharedComponentStoreIds,
        }, cancellationToken);

    private static bool IsDraftVersion(SiteBuilderSectionScanPage page) =>
        page.Page.Pages?.FirstOrDefault(x => x.Id == page.ContentPageId)?.Status == Draft;

    private static IEnumerable<PlacingPageRow> ToRows(IEnumerable<SiteBuilderSectionScanPage> pages, string version) =>
        pages
            .Where(x => x.Sections.Count > 0)
            .Select(x => new PlacingPageRow(x.Page.Id, x.Page.CultureName, CustomSectionText.Quote(x.Page.Name), x.Page.Permalink,
                x.Sections.Count(y => y.SharedComponentId == null),
                x.Sections.Select(y => y.SharedComponentId).Where(y => y != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                Version = version,
            });
}
