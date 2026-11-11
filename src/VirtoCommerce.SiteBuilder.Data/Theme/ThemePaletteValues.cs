using System.Text.RegularExpressions;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>What a palette may hold: the storefront sets each token as a CSS custom property.</summary>
public static class ThemePaletteValues
{
    // ASCII only: \d and \s would take other digits and spaces, which CSS does not
    private const string Space = "[ \\t\\n\\r\\f]";
    private const string Number = "[+-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:e[+-]?[0-9]+)?";
    private const string Channel = Number + "%?";
    private const string Hue = Number + "(?:deg|grad|rad|turn)?";

    private static readonly Regex ColorPattern = new(
        "\\A(?:" +
        "#(?:[0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})" +
        $"|rgba?\\({Space}*{Channel}{Space}*,{Space}*{Channel}{Space}*,{Space}*{Channel}{Space}*(?:,{Space}*{Channel}{Space}*)?\\)" +
        $"|rgba?\\({Space}*{Channel}{Space}+{Channel}{Space}+{Channel}{Space}*(?:/{Space}*{Channel}{Space}*)?\\)" +
        $"|hsla?\\({Space}*{Hue}{Space}*,{Space}*{Channel}{Space}*,{Space}*{Channel}{Space}*(?:,{Space}*{Channel}{Space}*)?\\)" +
        $"|hsla?\\({Space}*{Hue}{Space}+{Channel}{Space}+{Channel}{Space}*(?:/{Space}*{Channel}{Space}*)?\\)" +
        ")\\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // the storefront's preset tokens: color_primary_500
    private static readonly Regex TokenNamePattern = new("\\A[a-z0-9_]+\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>A hex color of 3, 4, 6 or 8 digits, or <c>rgb()</c>, <c>rgba()</c>, <c>hsl()</c>, <c>hsla()</c> with numbers.</summary>
    public static bool IsColor(string value) => value != null && ColorPattern.IsMatch(value);

    /// <summary>The shape of a token name, for when the storefront's list of tokens is not known.</summary>
    public static bool IsTokenName(string name) => name != null && TokenNamePattern.IsMatch(name);
}
