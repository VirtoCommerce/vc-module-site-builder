using System.Security.Claims;
using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemePublisher
{
    Task<int> PublishAsync(string storeId, string catalogHash, ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <param name="expectedVersions">The version of every draft, by its path with the theme folder; <c>null</c> publishes the drafts whatever they hold.</param>
    /// <exception cref="ThemeValidationException">The theme is not valid, or a palette draft it publishes leaves the palette short of the contrast the storefront needs.</exception>
    /// <exception cref="Models.SiteBuilderContentConflictException">The drafts are not those of <paramref name="expectedVersions"/>: nothing is published.</exception>
    Task<int> PublishAsync(string storeId, string catalogHash, IDictionary<string, string> expectedVersions, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the theme as <see cref="PublishAsync(string, string, IDictionary{string, string}, ClaimsPrincipal, CancellationToken)"/> does and
    /// answers the drafts it would publish, writing nothing; a live palette that fails the contrast and that the publication leaves as it is
    /// is told as a warning.
    /// </summary>
    /// <exception cref="ThemeValidationException">The theme is not valid, or a palette draft it publishes leaves the palette short of the contrast the storefront needs.</exception>
    Task<ThemePublicationPreview> PreviewPublishAsync(string storeId, string catalogHash, CancellationToken cancellationToken = default);

    /// <summary>Publishes the live theme files when the store has no publication yet; otherwise returns the current publication.</summary>
    Task<int> PublishFirstAsync(string storeId, CancellationToken cancellationToken = default);

    Task<ThemeStatus> GetStatusAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>The files are keyed by their path in the theme.</summary>
    /// <exception cref="ThemeValidationException">There is no such catalog, or a file does not match it.</exception>
    Task ValidateFilesAsync(string storeId, string catalogHash, IDictionary<string, JToken> files, CancellationToken cancellationToken = default);
}
