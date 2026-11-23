namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>The version of a page kept in git, committed again on the editor's own work branch.</summary>
public class FilePageVersionRestore
{
    public string Branch { get; set; }

    public string CommitSha { get; set; }

    /// <summary>The commit of the version restored.</summary>
    public string RestoredFrom { get; set; }
}
