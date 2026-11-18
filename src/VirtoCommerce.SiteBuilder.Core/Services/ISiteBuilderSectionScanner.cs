using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// Finds the sections of a type in page and template documents, in the current and the older page shape. It checks no
/// user: the caller narrows the stores to those the user may read.
/// </summary>
public interface ISiteBuilderSectionScanner
{
    Task<SiteBuilderSectionScanResult> ScanPagesAsync(SiteBuilderSectionScanCriteria criteria, CancellationToken cancellationToken = default);

    /// <summary>The templates of the store's current theme.</summary>
    Task<SiteBuilderTemplateSectionScanResult> ScanTemplatesAsync(SiteBuilderTemplateSectionScanCriteria criteria,
        CancellationToken cancellationToken = default);
}
