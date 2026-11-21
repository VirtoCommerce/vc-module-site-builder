using System.Globalization;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>A theme's custom section types: its files in <see cref="ThemeStorage.CustomSectionsFolder"/>, and the schemas they add to the catalog.</summary>
public static class ThemeCustomSections
{
    public const string CustomKey = "custom";
    private const string NameKey = "name";
    private const string SchemaKey = "schema";

    // what of a type's schema the storefront adds to the catalog, as it adds a type
    private static readonly string[] CatalogSchemaKeys = ["settings", "targetTemplates", "limit", "icon"];

    /// <summary>The rule a type's name follows, for a text that tells it.</summary>
    public static string DescribeType() =>
        string.Create(CultureInfo.InvariantCulture, $"matches {ThemeStorage.CustomSectionTypePattern} and has at most {ThemeStorage.CustomSectionTypeMaxLength} characters");

    /// <summary>Each error after the file it is of, as a theme's errors are written.</summary>
    public static IEnumerable<string> GetErrors(IEnumerable<CustomSectionValidationResult> results) =>
        results.SelectMany(x => x.Errors.Select(error => $"{x.File}: {error}"));

    /// <summary>Every type the folder names, valid or not, for a check to refuse a name that is not one.</summary>
    public static async Task<IList<string>> ListTypesAsync(IThemeStorage storage)
    {
        var fileNames = await storage.ListFilesAsync(ThemeStorage.CustomSectionsFolder);

        // a draft listed by its draft file names the same type as its live file
        return fileNames
            .Select(x => ThemeStorage.GetCustomSectionType(storage.GetLivePath(x)))
            .Where(x => x != null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether the document places a custom type anywhere, as the type of an item.</summary>
    public static bool PlacesCustomType(JToken document) =>
        document is JContainer container && container.Descendants().OfType<JProperty>()
            .Any(x => x.Name == "type" && x.Value is JValue { Type: JTokenType.String } type && ThemeStorage.IsCustomSectionType((string)type));

    /// <summary>The storefront renders only the group's own sections in the header and the footer, so neither places a custom type.</summary>
    public static IEnumerable<string> GetSectionGroupErrors(string file, JToken sectionGroup) =>
        ((sectionGroup as JObject)?["content"] as JArray)?.OfType<JObject>().Where(IsPlacement)
            .Select(x => $"{file}: '{ThemeJson.GetString(x, "id")}' places a custom section, which only a page or a template can hold")
        ?? [];

    public static bool IsSectionGroupFile(string pathInTheme) =>
        pathInTheme.Equals(ThemeStorage.HeaderGroupFile, StringComparison.OrdinalIgnoreCase) ||
        pathInTheme.Equals(ThemeStorage.FooterGroupFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>An item of a page document that places a custom type.</summary>
    public static bool IsPlacement(JObject item) =>
        !string.IsNullOrWhiteSpace(ThemeJson.GetString(item, "id")) && ThemeJson.GetString(item, "type") is { } type && ThemeStorage.IsCustomSectionType(type);

    /// <summary>The type of a file by its path in the theme, valid or not; null for a file outside the folder or one that is not JSON.</summary>
    public static string GetCustomSectionType(string pathInTheme)
    {
        var folder = ThemeStorage.CustomSectionsFolder + "/";

        return pathInTheme.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? ThemeStorage.GetCustomSectionType(pathInTheme[folder.Length..]) : null;
    }

    /// <summary>
    /// The catalog with the theme's types in its sections, as the storefront adds them, in place of the custom types the storefront's preview saw:
    /// a type just written is in no catalog yet, and an older catalog may hold a type the theme no longer has. Null for no catalog.
    /// </summary>
    public static JObject AddToCatalog(JObject catalog, IDictionary<string, JToken> types)
    {
        if (catalog == null)
        {
            return null;
        }

        var result = (JObject)catalog.DeepClone();
        if (result[ModuleConstants.SchemaKinds.Sections] is not JObject sections)
        {
            sections = new JObject();
            result[ModuleConstants.SchemaKinds.Sections] = sections;
        }

        foreach (var custom in sections.Properties().Where(x => x.Value is JObject schema && IsCustom(schema)).ToList())
        {
            custom.Remove();
        }

        foreach (var (type, content) in types.Where(x => ThemeStorage.IsCustomSectionType(x.Key)))
        {
            if (GetCatalogSchema(content) is { } schema)
            {
                sections[type] = schema;
            }
        }

        return result;
    }

    public static bool IsCustom(JObject schema) => schema[CustomKey] is JValue { Type: JTokenType.Boolean } custom && (bool)custom;

    private static JObject GetCatalogSchema(JToken content)
    {
        if (content is not JObject type || type[SchemaKey] is not JObject schema)
        {
            return null;
        }

        var result = new JObject();
        if (type[NameKey] is { } name)
        {
            result[NameKey] = name.DeepClone();
        }

        foreach (var key in CatalogSchemaKeys.Where(x => schema[x] != null))
        {
            result[key] = schema[key].DeepClone();
        }

        result[CustomKey] = true;

        return result;
    }
}
