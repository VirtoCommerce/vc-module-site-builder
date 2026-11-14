namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A page group to create, or to update with the settings it holds.</summary>
public class GroupedPageSaveOperationRequest
{
    public GroupedSiteBuilderPage Page { get; set; }

    /// <summary>Checks the write as it would run and answers what it would change instead of changing it.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// A write that runs also tells <see cref="SiteBuilderOperationResult.AffectsPublished"/>, at the cost of the reads that tell it.
    /// A dry run always tells it; a write without this answers <c>false</c>.
    /// </summary>
    public bool ReportImpact { get; set; }

    /// <summary>An update is refused when it would change what a published page serves, as read under the lock of its save. Not read by a create.</summary>
    public bool RequireUnpublished { get; set; }

    /// <summary>
    /// The version of the settings the caller reviewed, as a dry run of the update tells it in <see cref="SiteBuilderObjectChange.ETag"/>: the update
    /// is refused once someone changed them. <c>null</c> writes over whatever they hold. Not read by a create.
    /// </summary>
    public string ExpectedSettingsVersion { get; set; }
}
