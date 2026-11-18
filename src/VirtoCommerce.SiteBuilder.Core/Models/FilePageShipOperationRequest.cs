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
    /// The version of the content the caller reviewed (<see cref="SiteBuilderContentVersion.CreateForFileContent"/>), as a dry run answers it in
    /// <see cref="SiteBuilderObjectChange.ETag"/>: for a publish in blob storage, the draft, else what is published; for an unpublish, what is
    /// published. It is read again right before the write, which is refused when it differs. A publish in git does not check it.
    /// <c>null</c> takes the page as it is.
    /// </summary>
    public string ExpectedContentVersion { get; set; }
}
