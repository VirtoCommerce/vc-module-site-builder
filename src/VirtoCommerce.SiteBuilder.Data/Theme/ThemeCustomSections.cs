using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>A theme's custom section types: its files in <see cref="ThemeStorage.CustomSectionsFolder"/>, and the schemas they add to the catalog.</summary>
public static class ThemeCustomSections
{
    private const string CustomKey = "custom";
    private const string NameKey = "name";
    private const string SchemaKey = "schema";

    // what of a type's schema the storefront adds to the catalog, as it adds a type
    private static readonly string[] CatalogSchemaKeys = ["settings", "targetTemplates", "limit", "icon"];

    /// <summary>The type of each file in the folder, drafts included; a name that is not a type is kept, for the validation to refuse.</summary>
    public static async Task<IList<string>> GetTypesAsync(IThemeStorage storage)
    {
        var fileNames = await storage.ListFilesAsync(ThemeStorage.CustomSectionsFolder);

        return fileNames
            .Select(x => ThemeStorage.GetCustomSectionType(storage.GetLivePath(x)))
            .Where(x => x != null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The type of a file by its path in the theme, valid or not; null for a file outside the folder or one that is not JSON.</summary>
    public static string GetSectionType(string file)
    {
        var folder = ThemeStorage.CustomSectionsFolder + "/";

        return file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? ThemeStorage.GetCustomSectionType(file[folder.Length..]) : null;
    }

    /// <summary>
    /// The catalog with the theme's types in its sections, as the storefront adds them, in place of the custom types the storefront's preview saw:
    /// a type just written is in no catalog yet, and an older catalog may hold a type the theme no longer has. Null for no catalog.
    /// </summary>
    /// <param name="types">The file content of each type by its type.</param>
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
