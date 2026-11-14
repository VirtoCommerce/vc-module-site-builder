namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A multilanguage page to create, or to update with the values it holds.</summary>
public class MultilanguagePageSaveOperationRequest
{
    public MultilanguagePage Page { get; set; }

    /// <summary>Checks the write as it would run and answers what it would change instead of changing it.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// A write that runs also tells <see cref="SiteBuilderOperationResult.AffectsPublished"/>, at the cost of the reads that tell it.
    /// A dry run always tells it; a write without this answers <c>false</c>.
    /// </summary>
    public bool ReportImpact { get; set; }

    /// <summary>
    /// An update is refused when it would change what a published language version serves, as the settings it passes on to the versions are
    /// compared under the lock of each save. Not read by a create.
    /// </summary>
    public bool RequireUnpublished { get; set; }

    /// <summary>
    /// The version of the multilanguage page the caller reviewed, as a dry run of the update tells it in <see cref="SiteBuilderObjectChange.ETag"/>:
    /// the update is refused once someone changed it. <c>null</c> writes over whatever it holds. Not read by a create.
    /// </summary>
    public string ExpectedSettingsVersion { get; set; }
}
