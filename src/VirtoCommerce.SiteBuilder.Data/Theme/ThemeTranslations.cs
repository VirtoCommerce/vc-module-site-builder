using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>A text per language in theme content, as the snapshot translates it and the designer reads it.</summary>
public static class ThemeTranslations
{
    private static readonly Regex CultureNamePattern = new("^[a-z]{2,3}(-[a-z0-9]{2,8})*$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    // A dictionary has a culture name with a subtag, so that a row such as { "sku": "X1" } stays a row.
    private static readonly Regex SubtaggedCultureNamePattern = new("^[a-z]{2,3}(-[a-z0-9]{2,8})+$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public static bool IsTranslation(JObject dictionary) =>
        dictionary.Properties().Any(x => SubtaggedCultureNamePattern.IsMatch(x.Name)) &&
        dictionary.Properties().All(x => CultureNamePattern.IsMatch(x.Name) && x.Value.Type == JTokenType.String);
}
