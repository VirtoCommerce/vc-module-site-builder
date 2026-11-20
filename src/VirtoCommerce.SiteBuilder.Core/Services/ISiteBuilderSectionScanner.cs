using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>Checks no permission: the caller narrows the stores to those the user may read.</summary>
public interface ISiteBuilderSectionScanner
{
    Task<SiteBuilderSectionScanResult> ScanPagesAsync(SiteBuilderSectionScanCriteria criteria, CancellationToken cancellationToken = default);

    Task<SiteBuilderTemplateSectionScanResult> ScanTemplatesAsync(SiteBuilderTemplateSectionScanCriteria criteria,
        CancellationToken cancellationToken = default);
}
