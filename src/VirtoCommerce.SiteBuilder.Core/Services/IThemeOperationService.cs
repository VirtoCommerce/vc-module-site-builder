using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>The publication of a store's theme, checking the user and the request itself: store access first, then the request.</summary>
public interface IThemeOperationService
{
    /// <summary>
    /// Publishes the theme with its drafts, or previews the publication on a dry run: every draft it would put live, with its version and who
    /// last changed it. A theme whose palette fails the WCAG 2.2 AA contrast is refused, with the failing pairs.
    /// </summary>
    Task<SiteBuilderOperationResult<ThemePublicationPointer>> PublishAsync(ThemePublishOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>The current publication and the files with drafts.</summary>
    Task<SiteBuilderOperationResult<ThemeStatus>> GetStatusAsync(SiteBuilderOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
