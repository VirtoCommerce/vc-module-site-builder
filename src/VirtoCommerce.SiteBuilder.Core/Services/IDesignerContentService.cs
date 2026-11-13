using System.Security.Claims;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>Writes the drafts of a page and of theme files together: all or nothing, one version of the history.</summary>
public interface IDesignerContentService
{
    /// <summary>Also sanitizes the HTML the files store, in place.</summary>
    /// <param name="files">Contents by the path with the theme folder.</param>
    /// <exception cref="InvalidDataException">A live theme write on a store with the new Site Builder.</exception>
    /// <exception cref="Theme.ThemeValidationException">A file can run script, or does not fit the catalog.</exception>
    Task ValidateThemeFilesAsync(string storeId, IDictionary<string, JToken> files, bool draft, string catalogHash,
        CancellationToken cancellationToken = default);

    /// <summary>Saves the content with its HTML sanitized.</summary>
    /// <exception cref="SiteBuilderContentConflictException">The page or a theme file was saved by someone else since it was read: nothing is written.</exception>
    /// <exception cref="InvalidDataException">Something in the content can run script: nothing is written.</exception>
    Task<DesignerSaveResult> SaveAsync(DesignerSaveCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>Sanitizes the content as <see cref="SaveAsync"/> does and answers what it would write over what the drafts hold now, writing nothing.</summary>
    /// <exception cref="InvalidDataException">Something in the content can run script.</exception>
    Task<SiteBuilderChangePreview> PreviewSaveAsync(DesignerSaveCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>A theme file never published keeps its draft.</summary>
    /// <exception cref="InvalidDataException">The page has never been published.</exception>
    Task<ContentSaveResult> DiscardAsync(DesignerDiscardCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>Answers the drafts <see cref="DiscardAsync"/> would throw away, and what each goes back to. Writes nothing.</summary>
    /// <exception cref="InvalidDataException">The page has never been published.</exception>
    Task<SiteBuilderChangePreview> PreviewDiscardAsync(DesignerDiscardCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>Answers what discarding the drafts of the page would change: its content goes back to what is published. Writes nothing.</summary>
    /// <exception cref="InvalidDataException">The page has never been published.</exception>
    Task<SiteBuilderChangePreview> PreviewDiscardPageAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken = default);

    /// <exception cref="SiteBuilderContentConflictException">A draft is not at the version the command expects it at: nothing is written.</exception>
    /// <exception cref="InvalidDataException">Something in the version can run script: nothing is written.</exception>
    Task<ContentSaveResult> RestoreAsync(ContentRestoreCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>Sanitizes the version as <see cref="RestoreAsync"/> does and answers the drafts it would overwrite, writing nothing.</summary>
    /// <exception cref="InvalidDataException">Something in the version can run script.</exception>
    Task<SiteBuilderChangePreview> PreviewRestoreAsync(ContentRestoreCommand command, CancellationToken cancellationToken = default);
}
