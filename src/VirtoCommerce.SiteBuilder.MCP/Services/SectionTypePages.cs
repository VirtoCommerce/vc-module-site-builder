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
public static class SectionTypePages
{
    public const int MaxPages = 50;

    private const string PublishedVersion = "published";

    private const string DraftVersion = "draft";

    private const string TemplatesNotScanned = " Templates are not scanned: search_templates lists them.";

    /// <param name="published"><c>true</c> reads the published version of each page, <c>false</c> its draft, else its published version.</param>
    public static async Task<(IList<PlacingPageRow> Pages, string Coverage)> FindAsync(ISiteBuilderSectionScanner sectionScanner,
        IAuthorizationService authorizationService, Func<UserManager<ApplicationUser>> userManagerFactory, ClaimsPrincipal user, string storeId, string type,
        bool published, CancellationToken cancellationToken)
    {
        var storeIds = await authorizationService.GetStoreIdsAsync(user, userManagerFactory, storeId, SharedComponentsRead);
        var scan = await ScanAsync(sectionScanner, storeId, type, published, storeIds, cancellationToken);

        return (ToRows(scan.Pages, version: null).ToList(), SectionScanCoverage.Describe(scan) + TemplatesNotScanned);
    }

    /// <summary>
    /// The pages whose published version places the type and those whose draft does, a row per version, each marked by <see cref="PlacingPageRow.Version"/>.
    /// </summary>
    public static async Task<(IList<PlacingPageRow> Pages, string Coverage)> FindPublishedAndDraftsAsync(ISiteBuilderSectionScanner sectionScanner,
        IAuthorizationService authorizationService, Func<UserManager<ApplicationUser>> userManagerFactory, ClaimsPrincipal user, string storeId, string type,
        CancellationToken cancellationToken)
    {
        var storeIds = await authorizationService.GetStoreIdsAsync(user, userManagerFactory, storeId, SharedComponentsRead);
        var published = await ScanAsync(sectionScanner, storeId, type, published: true, storeIds, cancellationToken);
        var drafts = await ScanAsync(sectionScanner, storeId, type, published: false, storeIds, cancellationToken);

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

        return (pages, SectionScanCoverage.Describe(scan) + " Each page was read in its published version and in its draft, where it has one." +
            TemplatesNotScanned);
    }

    private static Task<SiteBuilderSectionScanResult> ScanAsync(ISiteBuilderSectionScanner sectionScanner, string storeId, string type, bool published,
        IList<string> sharedComponentStoreIds, CancellationToken cancellationToken) =>
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
