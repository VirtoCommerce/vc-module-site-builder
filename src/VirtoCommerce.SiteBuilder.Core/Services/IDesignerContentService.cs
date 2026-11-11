using System.Security.Claims;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>Writes the drafts of a page and of theme files together: all or nothing, one version of the history.</summary>
public interface IDesignerContentService
{
    /// <param name="files">Contents by the path with the theme folder.</param>
    /// <exception cref="InvalidDataException">A live theme write on a store with the new Site Builder.</exception>
    Task ValidateThemeFilesAsync(string storeId, IDictionary<string, JToken> files, bool draft, string catalogHash,
        CancellationToken cancellationToken = default);

    /// <exception cref="SiteBuilderContentConflictException">The page or a theme file was saved by someone else since it was read: nothing is written.</exception>
    Task<DesignerSaveResult> SaveAsync(DesignerSaveCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>A theme file never published keeps its draft.</summary>
    /// <exception cref="InvalidDataException">The page has never been published.</exception>
    Task<ContentSaveResult> DiscardAsync(DesignerDiscardCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    Task<ContentSaveResult> RestoreAsync(ContentRestoreCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default);
}
