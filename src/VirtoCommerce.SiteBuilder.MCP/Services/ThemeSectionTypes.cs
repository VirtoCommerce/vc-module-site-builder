using System.Text.Json.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

// A custom section type of a store's theme, as its draft holds it, else as it is live. The storefront renders only a published one.
public sealed record ThemeSectionType(string Type, string Content, bool Published, bool HasDraft);

public static class ThemeSectionTypes
{
    public static async Task<IList<ThemeSectionType>> ListAsync(IThemeStorageFactory themeStorageFactory, string storeId)
    {
        var storage = await themeStorageFactory.OpenAsync(storeId);
        var result = new List<ThemeSectionType>();
        foreach (var type in (await ThemeCustomSections.GetTypesAsync(storage)).Where(ThemeStorage.IsCustomSectionType).Order(StringComparer.Ordinal))
        {
            result.Add(await ReadAsync(storage, type));
        }

        return result;
    }

    public static async Task<ThemeSectionType> ReadAsync(IThemeStorage storage, string type)
    {
        var file = ThemeStorage.GetCustomSectionFile(type);
        var draft = await storage.ReadAsync(storage.GetDraftPath(file));
        var published = await storage.ExistsAsync(file);

        return new ThemeSectionType(type, draft ?? (published ? await storage.ReadAsync(file) : null), published, draft != null);
    }

    // As the save checks content: the theme's types in place of the custom ones the storefront's preview saw, which may not know a new one yet.
    public static JsonObject AddToCatalog(JsonObject catalog, IEnumerable<ThemeSectionType> types)
    {
        if (catalog == null)
        {
            return null;
        }

        var contents = new Dictionary<string, JToken>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (Parse(type.Content) is { } content)
            {
                contents[type.Type] = content;
            }
        }

        var result = ThemeCustomSections.AddToCatalog(ThemeJson.ParseObject(catalog.ToJsonString()), contents);

        return JsonNode.Parse(result.ToString(Formatting.None)) as JsonObject;
    }

    private static JToken Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return ThemeJson.Parse(content);
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }
}
