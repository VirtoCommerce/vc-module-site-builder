namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A publish or an unpublish of a page or a blog article kept as a file.</summary>
public class FilePageShipOperationRequest : FilePageDraftOperationRequest
{
    /// <summary>
    /// The user name whose draft branch is shipped, when someone other than its editor ships it; <c>null</c> or blank ships the caller's own.
    /// Only the branch changes: the access is the caller's, and so are the commits. Blob storage keeps one draft for every editor, so there it is not read.
    /// </summary>
    public string DraftOwner { get; set; }
}
