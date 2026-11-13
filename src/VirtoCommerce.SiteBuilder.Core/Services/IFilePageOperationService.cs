using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// Writes of a page or a blog article kept as a file, in git or in blob storage, each checking the user and the request itself: store access
/// first, then the request. Every write takes a dry run, and tells whether it reaches what the storefront serves. Git failing is not an
/// outcome but a <see cref="GitContentException"/>.
/// </summary>
public interface IFilePageOperationService
{
    /// <summary>
    /// Merges the editor's draft branch into the base branch, or publishes the draft in blob storage. A merge that conflicts is an outcome
    /// told in the shipment; publishing again with <see cref="FilePagePublishOperationRequest.Rebase"/> is its way out. With no draft the
    /// page is already published. Where the platform keeps the live copy it is written again from what is published.
    /// A page that is not a page document is refused with the problems one by one.
    /// </summary>
    Task<SiteBuilderOperationResult<FilePageShipment>> PublishAsync(FilePagePublishOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the page on the editor's branch and merges that, or unpublishes it in blob storage. A page the base branch does not hold
    /// is not found, unless it has been removed from there and only its live copy is left to remove.
    /// </summary>
    Task<SiteBuilderOperationResult<FilePageShipment>> UnpublishAsync(FilePageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the page as the base branch holds it onto the production branch, through a pull request of its own.
    /// Not found without git; refused without a production branch, or for a page not on the base branch.
    /// </summary>
    Task<SiteBuilderOperationResult<FilePageShipment>> PromoteAsync(FilePageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits a version of the page again, sanitized, on the editor's own work branch. Not found without git, and for a commit that
    /// does not hold the page: then with an empty value. A version that is not a page document, or can run script, is refused with
    /// the problems one by one.
    /// </summary>
    Task<SiteBuilderOperationResult<FilePageVersionRestore>> RestoreVersionAsync(FilePageRestoreVersionOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits a copy of the page as the editor sees it to their work branch. Not found without git. A source that cannot be copied is
    /// refused with a value naming it in its path.
    /// </summary>
    Task<SiteBuilderOperationResult<GitPageCommit>> CopyAsync(FilePageCopyOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the editor's draft branch of the page. Not found without git.</summary>
    Task<SiteBuilderOperationResult> DiscardGitDraftAsync(FilePageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the draft in blob storage of a page, a blog article or a theme file; a theme file's in the content history too.
    /// Refused, with a value telling why, for a page of a store that keeps its pages in git, and for a draft that is the only copy.
    /// </summary>
    Task<SiteBuilderOperationResult<FileDraftDiscard>> DiscardDraftAsync(FileDraftDiscardOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
