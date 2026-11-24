using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

/// <summary>The pages that place a custom section type, as far as one bounded scan reads them.</summary>
public static class SectionTypePages
{
    public const int MaxPages = 50;

    /// <param name="published"><c>true</c> reads the published version of each page, <c>false</c> its draft, else its published version.</param>
    public static async Task<(IList<PlacingPageRow> Pages, string Coverage)> FindAsync(ISiteBuilderSectionScanner sectionScanner,
        IAuthorizationService authorizationService, Func<UserManager<ApplicationUser>> userManagerFactory, ClaimsPrincipal user, string storeId, string type,
        bool published, CancellationToken cancellationToken)
    {
        var scan = await sectionScanner.ScanPagesAsync(new SiteBuilderSectionScanCriteria
        {
            Pages = new SiteBuilderPageSearchCriteria { StoreId = storeId, Take = MaxPages },
            MaxPages = MaxPages,
            Published = published,
            SectionType = type,
            SharedComponentStoreIds = await authorizationService.GetStoreIdsAsync(user, userManagerFactory, storeId, SharedComponentsRead),
        }, cancellationToken);

        var pages = scan.Pages
            .Where(x => x.Sections.Count > 0)
            .Select(x => new PlacingPageRow(x.Page.Id, x.Page.CultureName, CustomSectionText.Quote(x.Page.Name), x.Page.Permalink,
                x.Sections.Count(y => y.SharedComponentId == null),
                x.Sections.Select(y => y.SharedComponentId).Where(y => y != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        return (pages, SectionScanCoverage.Describe(scan) + " Templates are not scanned: search_templates lists them.");
    }
}
