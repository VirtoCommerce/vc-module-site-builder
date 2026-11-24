using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// One-time codes that let the storefront mint the preview user's token on its own host, whose issuer it accepts.
/// </summary>
public interface IPreviewAuthCodeService
{
    /// <summary>
    /// A code for one of the store's preview users, for an editor who works in the store. Forbidden for a user the store does not list.
    /// </summary>
    Task<SiteBuilderOperationResult<PreviewAuthCode>> CreateCodeAsync(PreviewAuthCodeOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>What the code carries, once: <c>null</c> for an expired, altered or already used code.</summary>
    Task<PreviewAuthCodeTicket> RedeemAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>The user is listed in the store's <see cref="ModuleConstants.Settings.StoreLevelSettings.PreviewUserIds"/> and is a customer account.</summary>
    Task<bool> IsPreviewUserAsync(string storeId, string userId);
}
