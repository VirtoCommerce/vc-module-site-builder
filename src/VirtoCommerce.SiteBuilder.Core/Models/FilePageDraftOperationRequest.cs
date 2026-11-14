namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A write of the draft branch of a page or a blog article kept in git.</summary>
public class FilePageDraftOperationRequest : FilePageOperationRequest
{
    /// <summary>
    /// The commit the draft branch was at when the caller reviewed the write, as a dry run answers it in <see cref="SiteBuilderObjectChange.DraftHead"/>:
    /// the write is refused once the branch is at another one, or gone. <c>null</c> takes the branch wherever it is.
    /// </summary>
    public string ExpectedDraftHead { get; set; }
}
