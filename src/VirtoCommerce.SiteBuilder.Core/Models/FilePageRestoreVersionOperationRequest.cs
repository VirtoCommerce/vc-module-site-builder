namespace VirtoCommerce.SiteBuilder.Core.Models;

public class FilePageRestoreVersionOperationRequest : FilePageOperationRequest
{
    /// <summary>The commit of the version to continue editing from.</summary>
    public string Sha { get; set; }

    /// <summary>
    /// The version of the draft the restore replaces, as a dry run tells it in <see cref="SiteBuilderObjectChange.ETag"/>: the caller's draft, else
    /// what is published, versioned by <see cref="SiteBuilderContentVersion.CreateForFileContent"/>. The commit is refused once the file changed;
    /// <c>null</c> commits over whatever the draft holds.
    /// </summary>
    public string ExpectedContentVersion { get; set; }
}
