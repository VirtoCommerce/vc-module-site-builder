using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>
/// The section that shows what a template's page is about: the product, the category's products, the page's own content.
/// The designer keeps it on every template, as a page without it shows nothing of what it is for.
/// </summary>
public static class ThemeTemplateMainSections
{
    private static readonly Dictionary<string, string> MainSections = new(StringComparer.Ordinal)
    {
        ["product"] = "product-info",
        ["category"] = "category-listing",
        ["page"] = "page-content",
    };

    /// <summary>The main section of a template, by its key; <c>null</c> for a key that names no template.</summary>
    public static string GetMainSection(string templateKey)
    {
        if (templateKey == null || !ThemeStorage.IsTemplateKey(templateKey))
        {
            return null;
        }

        return MainSections.GetValueOrDefault(templateKey.Split('.')[0]);
    }

    /// <summary>The template key of a file of the theme, as "templates/product.alternate.json" names it; <c>null</c> for any other file.</summary>
    public static string GetTemplateKey(string pathInTheme)
    {
        var folder = ThemeStorage.TemplatesFolder + "/";
        if (pathInTheme == null || !pathInTheme.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fileName = pathInTheme[folder.Length..];

        return fileName.Contains('/') ? null : ThemeStorage.GetTemplateKey(fileName);
    }

    /// <summary>
    /// Whether the template shows its main section, as the designer tells it: a storefront adds a missing main section itself and shows none
    /// for a hidden one. <c>null</c> for content that is not a page document.
    /// </summary>
    public static ThemeTemplateMainSectionState? GetState(JToken document, string mainSection)
    {
        IEnumerable<JToken> content;
        switch (document)
        {
            case JObject page when page["content"] == null:
                return ThemeTemplateMainSectionState.Missing;
            case JObject page when page["content"] is JArray sections:
                content = sections;
                break;
            case JArray legacy:
                content = legacy.Skip(1);
                break;
            default:
                return null;
        }

        var main = content.OfType<JObject>().Where(x => x["type"] is JValue { Type: JTokenType.String } type && (string)type == mainSection).ToList();
        if (main.Count == 0)
        {
            return ThemeTemplateMainSectionState.Missing;
        }

        return main.Exists(x => !IsHidden(x)) ? ThemeTemplateMainSectionState.Shown : ThemeTemplateMainSectionState.Hidden;
    }

    private static bool IsHidden(JObject section) => section["hidden"] is JValue { Type: JTokenType.Boolean } hidden && (bool)hidden;
}
