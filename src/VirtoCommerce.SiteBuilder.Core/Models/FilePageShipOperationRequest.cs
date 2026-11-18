namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A publish or an unpublish of a page or a blog article kept as a file.</summary>
public class FilePageShipOperationRequest : FilePageDraftOperationRequest
{
    /// <summary>
    /// The user name whose draft branch is shipped, when someone other than its editor ships it; <c>null</c> or blank ships the caller's own.
    /// Only the branch changes: the access is the caller's, and so are the commits. Blob storage keeps one draft for every editor, so there it is not read.
    /// </summary>
    public string DraftOwner { get; set; }

    /// <summary>
    /// The version of the content the caller reviewed, as <see cref="SiteBuilderContentVersion.CreateForFileContent"/> makes it of what the page reads
    /// and a dry run answers it in <see cref="SiteBuilderObjectChange.ETag"/>: in blob storage, for a publish, the draft, else what is published; for an
    /// unpublish, what is published, in git too, where it is what the production branch holds before the deletion is committed. It is read again right
    /// before the write, and the write is refused when it differs. A publish in git does not read it. <c>null</c> takes the page as it is.
    /// </summary>
    public string ExpectedContentVersion { get; set; }
}
