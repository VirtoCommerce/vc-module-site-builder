using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>
/// The sections the designer offers a template, as the storefront's schemas describe them: the ones its template schema lists, or,
/// without a list, the ones that name no template or name this one. A custom type is offered as if the template had no list.
/// </summary>
public static class ThemeTemplateSections
{
    private const string PagesType = "pages";

    /// <summary>A section is read as an id and a type; an item without both is left to the theme's validation.</summary>
    public readonly record struct Section(string Id, string Type);

    public static bool IsOffered(JObject catalog, string templateType, string sectionType)
    {
        var (key, template) = FindTemplate(catalog, templateType);
        var schema = (catalog?["sections"] as JObject)?[sectionType] as JObject;
        if (template?["sections"] is JArray sections)
        {
            return sections.Any(x => IsText(x, sectionType)) || (schema != null && ThemeCustomSections.IsCustom(schema) && IsTargeted(schema, key));
        }

        return schema == null || IsTargeted(schema, key);
    }

    /// <summary>The sections of a page document or of a legacy template, which starts with its settings; none for anything else.</summary>
    public static IList<Section> GetSections(JToken document)
    {
        var content = document switch
        {
            JObject page when page["content"] is JArray sections => sections,
            JArray legacy => legacy.Skip(1),
            _ => [],
        };

        return content.OfType<JObject>()
            .Select(x => new Section(ThemeJson.GetString(x, "id"), ThemeJson.GetString(x, "type")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Type))
            .ToList();
    }

    // as the designer finds it: by the template its preview shows, or by its own key
    private static (string Key, JObject Template) FindTemplate(JObject catalog, string templateType)
    {
        if (catalog?["templates"] is not JObject templates)
        {
            return (templateType, null);
        }

        var entries = templates.Properties().Where(x => x.Value is JObject).ToList();
        var entry = entries.Find(x => x.Value["previewMessage"] is JObject preview && IsText(preview["templateKey"], templateType))
            ?? entries.Find(x => x.Name == templateType && !IsText(x.Value["type"], PagesType));

        return entry == null ? (templateType, null) : (entry.Name, (JObject)entry.Value);
    }

    private static bool IsTargeted(JObject schema, string templateKey) =>
        schema["targetTemplates"] is not JArray { Count: > 0 } targets || targets.Any(x => IsText(x, templateKey));

    private static bool IsText(JToken token, string text) => token is JValue { Type: JTokenType.String } value && (string)value == text;
}
