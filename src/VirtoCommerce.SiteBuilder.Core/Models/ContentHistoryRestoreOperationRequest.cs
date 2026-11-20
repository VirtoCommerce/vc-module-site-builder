namespace VirtoCommerce.SiteBuilder.Core.Models;

public class ContentHistoryRestoreOperationRequest : ContentHistoryOperationRequest
{
    public string SaveId { get; set; }

    /// <summary>Checks and sanitizes the version as a restore does, and answers what it would overwrite instead of overwriting it.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// The version of the draft each item holds now, by the item as <see cref="ContentHistoryOperationRequest.Items"/> names it, as a dry run
    /// answers it. The restore is refused when a draft is not at that version any more. <c>null</c> restores over the drafts whatever they hold.
    /// </summary>
    public IDictionary<string, string> ExpectedVersions { get; set; }
}
