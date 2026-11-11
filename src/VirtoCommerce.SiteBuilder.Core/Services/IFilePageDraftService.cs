using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface IFilePageDraftService
{
    Task SaveDraftAsync(string storeId, SiteBuilderContentFile page, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    Task<GitPageCommit> CommitDraftAsync(string storeId, string repository, SiteBuilderContentFile page, string action, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    Task<DraftDiscardStatus> DiscardDraftAsync(string storeId, string contentType, string path, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    Task DiscardGitDraftAsync(string repository, string contentType, string path, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>Refused when nothing is published, as the draft is then the only copy.</summary>
    Task<DraftDiscardStatus> DiscardBlobDraftAsync(string storeId, string contentType, string path, string theme,
        CancellationToken cancellationToken = default);
}
