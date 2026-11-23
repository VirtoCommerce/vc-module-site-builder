namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageOperationRequest
{
    public string MultilanguagePageId { get; set; }

    /// <summary>Checks the write as it would run and answers what it would change instead of changing it.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// A write that runs also tells <see cref="SiteBuilderOperationResult.AffectsPublished"/>, at the cost of the reads that tell it.
    /// A dry run always tells it; a write without this answers <c>false</c>.
    /// </summary>
    public bool ReportImpact { get; set; }
}
