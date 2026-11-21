using System.Text.RegularExpressions;
using VirtoCommerce.AssetsModule.Core.Assets;
using VirtoCommerce.SiteBuilder.Data.Validation;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>The URLs a custom section type may write: an image of the store's asset library, and a link the storefront opens safely.</summary>
public class CustomSectionUrlPolicy(IBlobUrlResolver blobUrlResolver)
{
    private const string AssetProbe = "file";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // an escaped dot, slash or backslash, which a URL resolves as the character
    private static readonly Regex EscapedPathPattern = new("%(?:2[eEfF]|5[cC])", RegexOptions.None, RegexTimeout);
    private static readonly Regex AnchorPattern = new("^#[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.None, RegexTimeout);
    private static readonly Regex ImageDataUrlPattern = new("^data:image/[a-z0-9.+-]+[;,]", RegexOptions.IgnoreCase, RegexTimeout);

    /// <summary>
    /// A URL of the asset library starts with its public address, as the link of each of its files does, once its dot segments are resolved
    /// as a browser resolves them: '/../' or an escaped dot could otherwise climb out of the library on the same host.
    /// </summary>
    public virtual bool IsAssetUrl(string url)
    {
        if (GetAssetsBaseUrl() is not { } baseUrl || url.Any(char.IsControl) || url.Contains('\\'))
        {
            return false;
        }

        var unescaped = EscapedPathPattern.Replace(url, x => x.Value.ToLowerInvariant() switch { "%2e" => ".", "%2f" => "/", _ => "\\" });
        if (unescaped.Contains('\\') || EscapedPathPattern.IsMatch(unescaped))
        {
            return false;
        }

        return Uri.TryCreate(unescaped, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) &&
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var library) && uri.AbsoluteUri.StartsWith(library.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>data:image/ is allowed too: an image runs no script and loads nothing else.</summary>
    public virtual bool IsImageDataUrl(string url) => ImageDataUrlPattern.IsMatch(url);

    /// <summary>A URL parser drops tabs and line breaks, and reads '\' as '/', so either could make a path start with '//', another site.</summary>
    /// <param name="schemes">Besides a path starting with '/' or an anchor, such as "https:".</param>
    public virtual bool IsLink(string url, IEnumerable<string> schemes)
    {
        if (url.Length == 0 || url.Any(char.IsControl))
        {
            return false;
        }

        if (url.StartsWith('/'))
        {
            return url.Length == 1 || url[1] is not ('/' or '\\');
        }

        if (url.StartsWith('#'))
        {
            return AnchorPattern.IsMatch(url);
        }

        return ContentUrlScheme.GetScheme(url) is { } scheme && schemes.Contains(scheme + ":", StringComparer.OrdinalIgnoreCase) &&
            Uri.TryCreate(url, UriKind.Absolute, out _);
    }

    /// <summary>The public link of a file at the library's root without the file; <c>null</c> when the library gives none.</summary>
    protected virtual string GetAssetsBaseUrl()
    {
        string url;
        try
        {
            url = blobUrlResolver.GetAbsoluteUrl(AssetProbe);
        }
        catch (UriFormatException)
        {
            return null;
        }

        return url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && url.EndsWith("/" + AssetProbe, StringComparison.Ordinal)
            ? url[..^AssetProbe.Length]
            : null;
    }
}
