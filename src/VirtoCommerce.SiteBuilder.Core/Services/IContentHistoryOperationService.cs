using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// The content history of a store's pages and theme files, checking the user and the request itself: store access first, then the request,
/// then access to the pages it names.
/// </summary>
public interface IContentHistoryOperationService
{
    /// <summary>Whether the store keeps a content history.</summary>
    Task<SiteBuilderOperationResult<ContentHistoryStatusResult>> GetStatusAsync(SiteBuilderOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    Task<SiteBuilderOperationResult<ContentVersionPage>> ListAsync(ContentHistoryListOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>The content of the item in the version.</summary>
    Task<SiteBuilderOperationResult<string>> ReadVersionAsync(ContentHistoryOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the version of the items over their drafts, sanitized, as one version of the history. The permissions to edit the items are
    /// checked before the page is looked up, so that a page not found does not tell which pages exist. A dry run answers the drafts it would
    /// overwrite, with their versions; given them back in <see cref="ContentHistoryRestoreOperationRequest.ExpectedVersions"/>, the restore is
    /// refused as a conflict when a draft has changed since.
    /// </summary>
    Task<SiteBuilderOperationResult<ContentSaveResult>> RestoreAsync(ContentHistoryRestoreOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
