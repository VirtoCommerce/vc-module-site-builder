using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

public class ThemeSnapshotBuilder(CustomSectionUrlPolicy customSectionUrlPolicy) : ThemeContentReader, IThemeSnapshotBuilder
{
    private const int SnapshotFormat = 1;
    private const int TemplateRulesVersion = 2;
    private const string MenuFieldType = "menu";

    private const string ProductPageType = "product";
    private const string CategoryPageType = "category";

    private static readonly Regex CultureNamePattern = new("^[a-z]{2,3}(-[a-z0-9]{2,8})*$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    // A dictionary has a culture name with a subtag, so that a row such as { "sku": "X1" } stays a row.
    private static readonly Regex SubtaggedCultureNamePattern = new("^[a-z]{2,3}(-[a-z0-9]{2,8})+$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    // The storefront and the designer default to the same rules.
    public const string DefaultTemplateRulesJson = """
        {
          "product": [
            {
              "id": "default-b2c",
              "template": "product.b2c",
              "when": { "property": { "name": "VirtoFrontend_UI_Layout", "equals": "B2C" } }
            }
          ]
        }
        """;

    private static readonly JObject DefaultTemplateRules = JObject.Parse(DefaultTemplateRulesJson);

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
        DateFormatString = "O",
    };

    public virtual ThemeSnapshotSet Build(
        ThemeSource source,
        JObject catalog,
        string catalogHash,
        IList<string> languages,
        string defaultLanguage,
        int publication,
        DateTime publishedDate)
    {
        var snapshot = CreateSnapshot(source, catalog, catalogHash, publication, publishedDate);

        var result = AbstractTypeFactory<ThemeSnapshotSet>.TryCreateInstance();
        result.Files = GetSnapshotLanguages(languages, defaultLanguage).ToDictionary(
            x => x,
            x => JsonConvert.SerializeObject(TranslateSnapshot(snapshot, x, defaultLanguage), SerializerSettings),
            StringComparer.OrdinalIgnoreCase);

        return result;
    }

    protected virtual ThemeSnapshotContent CreateSnapshot(ThemeSource source, JObject catalog, string catalogHash, int publication, DateTime publishedDate)
    {
        var snapshot = AbstractTypeFactory<ThemeSnapshotContent>.TryCreateInstance();

        snapshot.Format = SnapshotFormat;
        snapshot.Publication = publication;
        snapshot.PublishedDate = publishedDate.ToUniversalTime();
        snapshot.Theme = source.ThemeName;
        snapshot.CatalogHash = catalogHash;
        snapshot.Rules = GetTemplateRules(source.Rules);
        snapshot.Templates = GetTemplates(source.Templates);
        snapshot.Header = GetSectionGroup(source.Header);
        snapshot.Footer = GetSectionGroup(source.Footer);
        snapshot.Brand = GetBrand(source.Brand, source.SettingsData);
        snapshot.Palette = GetPalette(source.Palette, source.PaletteDark);
        snapshot.CustomSections = GetCustomSections(source.CustomSections);
        snapshot.AssetsBaseUrl = customSectionUrlPolicy.GetAssetsBaseUrl();
        snapshot.Menus = GetMenus(catalog, snapshot.Header, snapshot.Footer);

        return snapshot;
    }

    protected virtual IList<string> GetSnapshotLanguages(IList<string> languages, string defaultLanguage)
    {
        return languages
            .Append(defaultLanguage)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    protected virtual ThemeSnapshotContent TranslateSnapshot(ThemeSnapshotContent snapshot, string language, string defaultLanguage)
    {
        var result = snapshot.CloneTyped();

        result.CultureName = language;
        result.Templates = result.Templates.ToDictionary(x => x.Key, x => (JObject)Translate(x.Value, language, defaultLanguage));
        result.Header = (JObject)Translate(result.Header, language, defaultLanguage);
        result.Footer = (JObject)Translate(result.Footer, language, defaultLanguage);
        result.CustomSections = result.CustomSections?.ToDictionary(x => x.Key, x => (JObject)Translate(x.Value, language, defaultLanguage));

        return result;
    }

    protected virtual IDictionary<string, JObject> GetTemplates(IDictionary<string, JToken> templates)
    {
        var result = new Dictionary<string, JObject>();

        foreach (var (key, document) in templates.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (ThemeStorage.IsTemplateKey(key) && GetPageDocument(document) is { } pageDocument)
            {
                RemoveSharedComponents(pageDocument);
                result[key] = pageDocument;
            }
        }

        return result;
    }

    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null leaves the custom section types out of the snapshot of a theme that has none.")]
    protected virtual IDictionary<string, JObject> GetCustomSections(IDictionary<string, JToken> customSections)
    {
        var result = customSections
            .Where(x => ThemeStorage.IsCustomSectionType(x.Key) && x.Value is JObject)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => (JObject)x.Value.DeepClone());

        return result.Count > 0 ? result : null;
    }

    protected virtual ThemeTemplateRules GetTemplateRules(JToken rules)
    {
        var source = rules as JObject ?? new JObject();
        var conditions = new SortedSet<string>(StringComparer.Ordinal);

        var result = AbstractTypeFactory<ThemeTemplateRules>.TryCreateInstance();
        result.Version = TemplateRulesVersion;
        result.Product = GetPageTypeRules(source, ProductPageType, conditions);
        result.Category = GetPageTypeRules(source, CategoryPageType, conditions);
        result.Page = GetPageTypeRules(source, ContentPageType, conditions);
        result.Conditions = conditions.ToList();

        return result;
    }

    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null leaves the page type out of the snapshot JSON; an empty list would be written as no templates for it.")]
    protected virtual IList<JObject> GetPageTypeRules(JObject rules, string pageType, ISet<string> conditions)
    {
        if ((rules[pageType] as JArray ?? DefaultTemplateRules[pageType] as JArray) is not { } list)
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<JObject>();

        foreach (var rule in list.OfType<JObject>().Where(IsTemplateRule))
        {
            var copy = (JObject)rule.DeepClone();
            var id = GetTemplateRuleId(pageType, result.Count, GetString(copy, "id"), ids);
            ids.Add(id);
            copy["id"] = id;

            if (copy["when"] is JObject when)
            {
                conditions.UnionWith(when.Properties().Select(x => x.Name));
            }

            result.Add(copy);
        }

        return result;
    }

    // Ids as the storefront and the designer make them, counting only the readable rules of the page type.
    public static string GetTemplateRuleId(string pageType, int index, string id, ISet<string> takenIds)
    {
        if (!string.IsNullOrEmpty(id) && !takenIds.Contains(id))
        {
            return id;
        }

        var generated = $"{pageType}-{index}";
        for (var n = 2; takenIds.Contains(generated); n++)
        {
            generated = $"{pageType}-{index}-{n}";
        }

        return generated;
    }

    protected virtual bool IsTemplateRule(JObject rule)
    {
        return rule["template"]?.Type == JTokenType.String && rule["when"] is null or JObject;
    }

    protected virtual JToken Translate(JToken token, string language, string defaultLanguage)
    {
        switch (token)
        {
            case JObject dictionary when IsTranslation(dictionary):
                return GetTranslation(dictionary, language, defaultLanguage);
            case JObject obj:
                foreach (var property in obj.Properties().ToList())
                {
                    property.Value = Translate(property.Value, language, defaultLanguage);
                }
                return obj;
            case JArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = Translate(array[i], language, defaultLanguage);
                }
                return array;
            default:
                return token;
        }
    }

    protected virtual bool IsTranslation(JObject dictionary)
    {
        return dictionary.Properties().Any(x => SubtaggedCultureNamePattern.IsMatch(x.Name))
            && dictionary.Properties().All(x => CultureNamePattern.IsMatch(x.Name) && x.Value.Type == JTokenType.String);
    }

    protected virtual string GetTranslation(JObject dictionary, string language, string defaultLanguage)
    {
        var languagePart = GetLanguagePart(language);

        string Find(Func<string, bool> matches) =>
            dictionary.Properties().FirstOrDefault(x => matches(x.Name))?.Value.Value<string>();

        return Find(x => string.Equals(x, language, StringComparison.OrdinalIgnoreCase))
            ?? Find(x => string.Equals(x, languagePart, StringComparison.OrdinalIgnoreCase))
            ?? Find(x => string.Equals(GetLanguagePart(x), languagePart, StringComparison.OrdinalIgnoreCase))
            ?? Find(x => string.Equals(x, defaultLanguage, StringComparison.OrdinalIgnoreCase))
            ?? dictionary.Properties().First().Value.Value<string>();
    }

    private static string GetLanguagePart(string cultureName)
    {
        return cultureName.Split('-')[0];
    }

    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null leaves the section group out of the snapshot; an empty object would be published as a group without content.")]
    protected virtual JObject GetSectionGroup(JToken token)
    {
        if (token is not JObject sectionGroup || sectionGroup["content"] is not JArray content)
        {
            return null;
        }

        var result = new JObject { ["content"] = content.DeepClone() };
        RemoveSharedComponents(result);
        RemoveCustomSections(result);

        return result;
    }

    // The storefront renders only a group's own sections in it; a first publication leaves a custom type out of the live files' groups.
    protected virtual void RemoveCustomSections(JObject sectionGroup)
    {
        foreach (var placement in ((JArray)sectionGroup["content"]).OfType<JObject>().Where(ThemeCustomSections.IsPlacement).ToList())
        {
            placement.Remove();
        }
    }

    // The storefront does not resolve shared components outside pages.
    protected virtual void RemoveSharedComponents(JObject document)
    {
        if (document["content"] is not JArray content)
        {
            return;
        }

        foreach (var placement in content.Where(x => x is JObject item && GetString(item, "type") == ModuleConstants.SharedComponents.ReferenceType).ToList())
        {
            placement.Remove();
        }
    }

    protected virtual ThemeBrand GetBrand(JToken brand, JToken settingsData)
    {
        var result = AbstractTypeFactory<ThemeBrand>.TryCreateInstance();

        if (brand is JObject brandFile)
        {
            result.LogoUrl = GetUrl(brandFile, "logoUrl");
            result.SecondaryLogoUrl = GetUrl(brandFile, "secondaryLogoUrl");
            result.FaviconUrl = GetUrl(brandFile, "faviconUrl");
        }
        else if (GetCurrentSettings(settingsData) is { } settings)
        {
            result.LogoUrl = GetUrl(settings, "logo_image");
            result.SecondaryLogoUrl = GetUrl(settings, "logo_inverted_image");
            result.FaviconUrl = GetUrl(settings, "favicon_image");
        }

        return result;
    }

    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null means the settings data has no current settings, which an override may tell apart from settings that set nothing.")]
    protected virtual JObject GetCurrentSettings(JToken settingsData)
    {
        if (settingsData is not JObject data)
        {
            return null;
        }

        return data["current"] switch
        {
            JObject current => current,
            JValue { Type: JTokenType.String } name => (data["presets"] as JObject)?[(string)name] switch
            {
                JObject preset when preset["settings"] is JObject nested => nested,
                JObject preset => preset,
                _ => null,
            },
            _ => null,
        };
    }

    protected virtual ThemePalette GetPalette(JToken light, JToken dark)
    {
        if (ThemePaletteValues.ReadColors(light) is not { } lightTokens)
        {
            return null;
        }

        var palette = AbstractTypeFactory<ThemePalette>.TryCreateInstance();
        palette.Light = lightTokens;
        palette.Dark = ThemePaletteValues.ReadColors(dark);

        return palette;
    }

    protected virtual IList<string> GetMenus(JObject catalog, params JObject[] sectionGroups)
    {
        if (catalog == null)
        {
            return [];
        }

        var menus = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sectionGroup in sectionGroups.Where(x => x != null))
        {
            CollectMenus(sectionGroup["content"] as JArray, ModuleConstants.SchemaKinds.Sections, catalog, menus);
        }

        return menus.ToList();
    }

    private void CollectMenus(JArray items, string kind, JObject catalog, ISet<string> menus)
    {
        foreach (var item in items?.OfType<JObject>() ?? [])
        {
            var fields = GetFields(GetItemSchema(catalog, kind, GetString(item, "type")), kind, catalog);
            CollectMenus(item, fields, menus);
            CollectMenus(item["blocks"] as JArray, ModuleConstants.SchemaKinds.Blocks, catalog, menus);
        }
    }

    private static void CollectMenus(JObject values, IList<JObject> fields, ISet<string> menus)
    {
        VisitValues(values, fields, path: null, (field, value, _) =>
        {
            if (GetString(field, "type") == MenuFieldType && value is JValue { Type: JTokenType.String } name && !string.IsNullOrWhiteSpace(name.Value<string>()))
            {
                menus.Add(name.Value<string>());
            }
        });
    }

    private static string GetUrl(JObject source, string name)
    {
        return source[name] is JValue { Type: JTokenType.String } value && !string.IsNullOrWhiteSpace(value.Value<string>()) ? value.Value<string>() : null;
    }
}
