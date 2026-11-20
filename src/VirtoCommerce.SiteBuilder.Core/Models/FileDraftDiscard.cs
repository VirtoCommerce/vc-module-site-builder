namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>What discarding a draft in blob storage did, or why it was refused.</summary>
public class FileDraftDiscard
{
    public DraftDiscardStatus Status { get; set; }

    /// <summary>Refused as the store keeps its pages in git: their drafts are discarded there.</summary>
    public bool KeptInGit { get; set; }
}
