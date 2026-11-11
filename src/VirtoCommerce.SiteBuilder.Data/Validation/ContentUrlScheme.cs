using AngleSharp.Html.Parser;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

/// <summary>The scheme of a URL as a browser reads it.</summary>
public static class ContentUrlScheme
{
    public const string JavaScript = "javascript";
    public const string VbScript = "vbscript";
    public const string Data = "data";

    private static readonly char[] ControlsAndSpace = Enumerable.Range(0, 0x21).Select(x => (char)x).ToArray();

    /// <summary>The schemes the value has, lower case: as written, and once its character references are decoded, as in an HTML attribute.</summary>
    public static IEnumerable<string> GetSchemes(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield break;
        }

        var scheme = GetScheme(value);
        if (scheme != null)
        {
            yield return scheme;
        }

        if (value.Contains('&') && GetScheme(DecodeAttribute(value)) is { } decoded && decoded != scheme)
        {
            yield return decoded;
        }
    }

    public static bool RunsScript(string scheme) => scheme is JavaScript or VbScript;

    /// <summary>A URL parser drops ASCII tabs and newlines anywhere and C0 controls and spaces before the URL.</summary>
    public static string GetScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon < 1)
        {
            return null;
        }

        var scheme = new string(value[..colon].Where(x => x is not ('\t' or '\n' or '\r')).ToArray()).TrimStart(ControlsAndSpace);
        if (scheme.Length == 0 || !char.IsAsciiLetter(scheme[0]) || !scheme.All(x => char.IsAsciiLetterOrDigit(x) || x is '+' or '-' or '.'))
        {
            return null;
        }

        return scheme.ToLowerInvariant();
    }

    // the HTML parser decodes the way a browser does, legacy references without a semicolon included
    private static string DecodeAttribute(string value)
    {
        var document = new HtmlParser().ParseDocument($"<a title=\"{value.Replace("\"", "&quot;", StringComparison.Ordinal)}\"></a>");

        return document.Body?.FirstElementChild?.GetAttribute("title") ?? value;
    }
}
