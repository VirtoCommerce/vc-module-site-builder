using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// Finds where sections of a type are used, reading the documents: the sections of a page or template at their places
/// in the document (not every nested object with a type), in both the current and the older page shape. It checks no
/// user: the caller narrows the stores to those the user may read.
/// </summary>
public interface ISiteBuilderSectionScanner
{
    Task<SiteBuilderSectionScanResult> ScanPagesAsync(SiteBuilderSectionScanCriteria criteria, CancellationToken cancellationToken = default);

    /// <summary>Templates of pages kept as theme files; pages kept as files are not scanned.</summary>
    Task<SiteBuilderTemplateSectionScanResult> ScanTemplatesAsync(SiteBuilderTemplateSectionScanCriteria criteria,
        CancellationToken cancellationToken = default);
}
