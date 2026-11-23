using VirtoCommerce.SiteBuilder.Core.GitContent;

namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>What publishing, unpublishing or promoting a page kept as a file did.</summary>
public class FilePageShipment
{
    /// <summary>The store keeps its pages in git; <c>false</c> for blob storage, where the page is published or unpublished at once.</summary>
    public bool GitFlow { get; set; }

    public GitPublishState State { get; set; }

    /// <summary>The merge that was asked for, with its pull request; <c>null</c> when there was nothing to merge, or no git.</summary>
    public GitPublishResult Merge { get; set; }

    /// <summary>For a merge that conflicts: why, and what the way out is.</summary>
    public string ConflictError { get; set; }

    /// <summary>A merge that conflicts has a way out from the builder: publishing the draft again with a rebase.</summary>
    public bool CanRebase { get; set; }
}
