using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

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

    private static readonly Regex TokenNamePattern = new("\\A[a-z0-9_]+\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsColor(string value) => value != null && ColorPattern.IsMatch(value);

    /// <summary>The shape of a token name, for when the storefront's list of tokens is not known.</summary>
    public static bool IsTokenName(string name) => name != null && TokenNamePattern.IsMatch(name);

    /// <summary>The colors of a palette file as the storefront gets them: a value that is not a color is left out, as the storefront would set it as a custom property as it is.</summary>
    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null means no color was set, so the snapshot has no palette; an empty one would replace the preset colors with nothing.")]
    public static Dictionary<string, string> ReadColors(JToken token)
    {
        var colors = (token as JObject)?.Properties()
            .Where(x => IsTokenName(x.Name) && x.Value is JValue { Type: JTokenType.String } value && IsColor((string)value))
            .ToDictionary(x => x.Name, x => (string)x.Value);

        return colors?.Count > 0 ? colors : null;
    }
}
