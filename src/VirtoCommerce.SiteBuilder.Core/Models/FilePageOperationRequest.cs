namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A write of a page or a blog article kept as a file.</summary>
public class FilePageOperationRequest : SiteBuilderOperationRequest
{
    /// <summary>pages or blogs; <c>null</c> for pages.</summary>
    public string ContentType { get; set; }

    public string Path { get; set; }

    /// <summary>Checks the write as it would run and answers what it would do instead of doing it.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// A write that runs also tells <see cref="SiteBuilderOperationResult.AffectsPublished"/>, at the cost of the reads that tell it.
    /// A dry run always tells it; a write without this answers <c>false</c>.
    /// </summary>
    public bool ReportImpact { get; set; }
}
