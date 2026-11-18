namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A write of the draft branch of a page or a blog article kept in git.</summary>
public class FilePageDraftOperationRequest : FilePageOperationRequest
{
    /// <summary>
    /// The commit the draft branch was at when the caller reviewed the write, as a dry run answers it in <see cref="SiteBuilderObjectChange.DraftHead"/>:
    /// the write is refused once the branch is at another one, or gone. <c>null</c> takes the branch wherever it is.
    /// A publish or an unpublish given one ships that commit from a branch of its own, so a commit made to the draft branch afterwards,
    /// while the merge waits for the repository's checks, is not merged with it.
    /// </summary>
    public string ExpectedDraftHead { get; set; }

    /// <summary>
    /// With no <see cref="ExpectedDraftHead"/>: the caller reviewed the write with no draft branch, as a dry run answers it with no
    /// <see cref="SiteBuilderObjectChange.DraftHead"/>, and the write is refused once there is one. An unpublish given it ships the deletion it commits
    /// from a branch of its own, as with <see cref="ExpectedDraftHead"/>. <c>false</c> takes the branch wherever it is, or none.
    /// </summary>
    public bool ExpectNoDraftBranch { get; set; }
}
