using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// The designer's reads and writes, each checking the user and the request itself: store access first, so that
/// a refusal for a bad request or a missing page does not tell a user of another store what exists in this one.
/// </summary>
public interface IDesignerOperationService
{
    /// <summary>The file as the designer edits it: its draft, else what is live.</summary>
    Task<SiteBuilderOperationResult<ThemeFileContent>> ReadThemeFileAsync(DesignerThemeFileRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Saves the drafts of a page and of theme files together, with their HTML sanitized, or previews the save on a dry run.</summary>
    Task<SiteBuilderOperationResult<DesignerSaveResult>> SaveAsync(DesignerSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Throws the drafts of a page and of theme files away together, or previews the discard on a dry run.</summary>
    Task<SiteBuilderOperationResult<ContentSaveResult>> DiscardAsync(DesignerDiscardOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Checks theme files about to be written: named once each, safe, and fit for the catalog. Sanitizes their HTML in place.</summary>
    Task<SiteBuilderOperationResult> ValidateThemeFilesAsync(string storeId, IList<SiteBuilderContentFile> files, bool draft, string catalogHash,
        CancellationToken cancellationToken = default);
}
