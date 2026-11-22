using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VirtoCommerce.AssetsModule.Core.Assets;
using VirtoCommerce.SiteBuilder.Data.Validation;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>The URLs a custom section type may write: an image of the store's asset library, and a link the storefront opens safely.</summary>
public class CustomSectionUrlPolicy(IBlobUrlResolver blobUrlResolver, ILogger<CustomSectionUrlPolicy> logger)
{
    private const string AssetProbe = "file";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex EscapedPathPattern = new("%(?:2[eEfF]|5[cC])", RegexOptions.None, RegexTimeout);
    private static readonly Regex AnchorPattern = new("^#[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.None, RegexTimeout);
    private static readonly Regex ImageDataUrlPattern = new("^data:image/[a-z0-9.+-]+[;,]", RegexOptions.IgnoreCase, RegexTimeout);

    /// <summary>
    /// A URL of the asset library is read as written, as the storefront reads it: its origin is the library's, and its path, once its
    /// dot segments are resolved, starts with the library's. An escaped dot, slash or backslash is refused: a reader that decodes it may
    /// end the host or climb out of the library elsewhere.
    /// </summary>
    public virtual bool IsAssetUrl(string url)
    {
        if (GetAssetsBaseUrl() is not { } baseUrl || url.Any(char.IsControl) || url.Contains('\\') || EscapedPathPattern.IsMatch(url))
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) &&
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var library) &&
            string.Equals(uri.GetLeftPart(UriPartial.Authority), library.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith(library.AbsolutePath, StringComparison.Ordinal);
    }

    /// <summary>data:image/ is allowed too: an image runs no script and loads nothing else.</summary>
    /// <remarks>Not with a raw '#': data:image/svg+xml with a fragment may load as a document whose images reach any host.</remarks>
    public virtual bool IsImageDataUrl(string url) => ImageDataUrlPattern.IsMatch(url) && !url.Contains('#');

    /// <summary>A URL parser drops tabs and line breaks, and reads '\' as '/', so either could make a path start with '//', another site; the storefront refuses both anywhere.</summary>
    /// <param name="schemes">Besides a path starting with '/' or an anchor, such as "https:".</param>
    public virtual bool IsLink(string url, IEnumerable<string> schemes)
    {
        if (url.Length == 0 || url.Any(char.IsControl) || url.Contains('\\'))
        {
            return false;
        }

        if (url.StartsWith('/'))
        {
            return url.Length == 1 || url[1] != '/';
        }

        if (url.StartsWith('#'))
        {
            return AnchorPattern.IsMatch(url);
        }

        return ContentUrlScheme.GetScheme(url) is { } scheme && schemes.Contains(scheme + ":", StringComparer.OrdinalIgnoreCase) &&
            Uri.TryCreate(url, UriKind.Absolute, out _);
    }

    /// <summary>The public link of a file at the library's root without the file; <c>null</c> when the library gives none.</summary>
    public virtual string GetAssetsBaseUrl()
    {
        string url;
        try
        {
            url = blobUrlResolver.GetAbsoluteUrl(AssetProbe);
        }
        // every publication reads it: a fault of the asset provider leaves custom sections without images, not the store without a theme
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The asset library gives no public address, so custom sections show no image.");
            return null;
        }

        return url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && url.EndsWith("/" + AssetProbe, StringComparison.Ordinal)
            ? url[..^AssetProbe.Length]
            : null;
    }
}
