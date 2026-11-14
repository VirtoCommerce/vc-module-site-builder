using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface IFilePageDraftService
{
    Task SaveDraftAsync(string storeId, SiteBuilderContentFile page, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the draft only over the draft the caller read, <see cref="ReadDraftAsync"/> versioned by
    /// <see cref="SiteBuilderContentVersion.CreateForFileContent"/>. In git the commit names the file it was compared with, so a commit made in
    /// between fails it; blob storage holds no lock over a draft, so there it is read right before the write.
    /// </summary>
    /// <exception cref="SiteBuilderContentConflictException">The draft is at another version: nothing is written.</exception>
    Task SaveDraftAsync(string storeId, SiteBuilderContentFile page, string expectedVersion, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>The page as the editor edits it: their draft, else what is published; <c>null</c> when there is neither.</summary>
    Task<string> ReadDraftAsync(string storeId, string contentType, string path, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>The page in blob storage: with <paramref name="draft"/>, its draft, else what is published; otherwise what is published. <c>null</c> when there is none.</summary>
    Task<string> ReadBlobPageAsync(string storeId, string contentType, string path, bool draft, CancellationToken cancellationToken = default);

    Task<GitPageCommit> CommitDraftAsync(string storeId, string repository, SiteBuilderContentFile page, string action, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    Task<DraftDiscardStatus> DiscardDraftAsync(string storeId, string contentType, string path, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>What <see cref="DiscardDraftAsync"/> would do, with the user's draft and what is published; writes nothing.</summary>
    Task<DraftDiscardPreview> PreviewDiscardDraftAsync(string storeId, string contentType, string path, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    Task DiscardGitDraftAsync(string repository, string contentType, string path, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>Refused when nothing is published, as the draft is then the only copy.</summary>
    Task<DraftDiscardStatus> DiscardBlobDraftAsync(string storeId, string contentType, string path, string theme,
        CancellationToken cancellationToken = default);

    /// <summary>What <see cref="DiscardBlobDraftAsync"/> would do, with the draft and what is published; writes nothing.</summary>
    Task<DraftDiscardPreview> PreviewDiscardBlobDraftAsync(string storeId, string contentType, string path, string theme,
        CancellationToken cancellationToken = default);
}
