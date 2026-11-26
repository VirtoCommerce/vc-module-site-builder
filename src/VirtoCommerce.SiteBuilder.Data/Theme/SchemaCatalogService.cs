using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

public class SchemaCatalogService(IThemeStorageFactory themeStorageFactory) : ISchemaCatalogService
{
    /// <summary>Of a schema's name and of a setting's label, or of each language's text of one: the agent reads them, as it reads an option's label.</summary>
    public const int MaxLabelLength = CustomSectionKit.MaxOptionLength;

    private const string HashProperty = "hash";
    private const string NameKey = "name";
    private const string LabelKey = "label";

    private static readonly string[] SchemaKinds =
    [
        ModuleConstants.SchemaKinds.Sections,
        ModuleConstants.SchemaKinds.Blocks,
        ModuleConstants.SchemaKinds.Shared,
    ];

    public virtual async Task<string> SaveAsync(string storeId, string catalogJson, CancellationToken cancellationToken = default)
    {
        ParseCatalog(catalogJson);
        var hash = GetHash(catalogJson);

        var storage = await themeStorageFactory.OpenAsync(storeId);
        if (await ReadCatalogAsync(storage, hash) == null)
        {
            await storage.WriteAsync(GetCatalogPath(hash), catalogJson);
        }

        var pointer = AbstractTypeFactory<SchemaCatalogPointer>.TryCreateInstance();
        pointer.Hash = hash;
        await storage.WriteJsonAsync(ThemeStorage.CurrentCatalogFile, pointer);

        return hash;
    }

    public virtual async Task<string> GetAsync(string storeId, string hash, CancellationToken cancellationToken = default)
    {
        var storage = await themeStorageFactory.OpenAsync(storeId);
        if (string.IsNullOrEmpty(hash))
        {
            hash = await ReadCurrentHashAsync(storage);
        }

        return IsValidHash(hash) ? await ReadCatalogAsync(storage, hash) : null;
    }

    public virtual async Task<string> GetCurrentHashAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var storage = await themeStorageFactory.OpenAsync(storeId);
        var hash = await ReadCurrentHashAsync(storage);

        return IsValidHash(hash) && await ReadCatalogAsync(storage, hash) != null ? hash : null;
    }

    protected virtual async Task<string> ReadCurrentHashAsync(IThemeStorage storage) =>
        (await storage.ReadJsonAsync<SchemaCatalogPointer>(ThemeStorage.CurrentCatalogFile))?.Hash;

    /// <summary>Null for none and for a file cut off mid-write, which the next upload of the hash then replaces.</summary>
    protected virtual async Task<string> ReadCatalogAsync(IThemeStorage storage, string hash)
    {
        var catalog = await storage.ReadAsync(GetCatalogPath(hash));
        if (catalog == null)
        {
            return null;
        }

        try
        {
            ThemeJson.ParseObject(catalog);
            return catalog;
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    protected virtual bool IsValidHash(string hash) => SiteBuilderContentPaths.IsCatalogHash(hash);

    protected virtual JObject ParseCatalog(string catalogJson)
    {
        JObject catalog;
        try
        {
            catalog = ThemeJson.ParseObject(catalogJson ?? string.Empty);
        }
        catch (JsonReaderException)
        {
            throw new ArgumentException("The catalog is not a JSON object.", nameof(catalogJson));
        }

        var malformed = SchemaKinds.FirstOrDefault(kind =>
            catalog[kind] is { Type: not JTokenType.Null } schemas && (schemas is not JObject map || map.Properties().Any(x => x.Value is not JObject)));
        if (malformed != null)
        {
            throw new ArgumentException($"The catalog's \"{malformed}\" must name a schema object for each key.", nameof(catalogJson));
        }

        if (CustomSectionKit.GetShapeError(catalog[CustomSectionKit.KitKey]) is { } kitError)
        {
            throw new ArgumentException($"The catalog's \"kit\" {kitError}.", nameof(catalogJson));
        }

        if (!IsPaletteTokens(catalog[CustomSectionKit.PaletteTokensKey]))
        {
            throw new ArgumentException($"The catalog's \"{CustomSectionKit.PaletteTokensKey}\" is not light and dark lists of at most " +
                $"{ThemePaletteTokensValidator.MaxTokens} token names each, of lowercase letters, digits and '_' and at most " +
                $"{ThemePaletteTokensValidator.MaxTokenLength} characters.", nameof(catalogJson));
        }

        if (FindLongLabeledSchema(catalog) is { } labeled)
        {
            throw new ArgumentException($"The catalog's schema \"{labeled.Name}\" has a key, a name or a label longer than {MaxLabelLength} characters.", nameof(catalogJson));
        }

        if (FindUnmarkedCustomSection(catalog) is { } section)
        {
            throw new ArgumentException($"The catalog's section \"{section.Name}\" starts with {ThemeStorage.CustomSectionTypePrefix}, which names the theme's " +
                "own types, and is not marked custom.", nameof(catalogJson));
        }

        return catalog;
    }

    // a custom type's section is the theme's, which the module reads from the theme in its place
    private static JProperty FindLongLabeledSchema(JObject catalog) =>
        ModuleConstants.SchemaKinds.All
            .SelectMany(kind => ((catalog[kind] as JObject)?.Properties() ?? [])
                .Where(x => kind != ModuleConstants.SchemaKinds.Sections || x.Value is not JObject section || !ThemeCustomSections.IsCustom(section)))
            .FirstOrDefault(x => x.Name.Length > MaxLabelLength || x.Value is JObject schema && !HasShortLabels(schema));

    // the rest of the module takes such a section for a type of the theme
    private static JProperty FindUnmarkedCustomSection(JObject catalog) =>
        (catalog[ModuleConstants.SchemaKinds.Sections] as JObject)?.Properties()
            .FirstOrDefault(x => x.Name.StartsWith(ThemeStorage.CustomSectionTypePrefix, StringComparison.Ordinal) && !ThemeCustomSections.IsCustom((JObject)x.Value));

    private static bool IsPaletteTokens(JToken tokens) =>
        tokens is null || tokens.Type == JTokenType.Null ||
        (tokens is JObject schemes && schemes.Properties().All(x => x.Name is ModuleConstants.Palette.Light or ModuleConstants.Palette.Dark && IsTokenList(x.Value)));

    private static bool IsTokenList(JToken tokens) =>
        tokens.Type == JTokenType.Null ||
        (tokens is JArray names && names.Count <= ThemePaletteTokensValidator.MaxTokens &&
            names.All(x => x is JValue { Type: JTokenType.String } name && ThemePaletteTokensValidator.IsToken((string)name)));

    private static bool HasShortLabels(JObject schema) =>
        IsShortText(schema[NameKey]) && schema.Descendants().OfType<JProperty>().Where(x => x.Name == LabelKey).All(x => IsShortText(x.Value));

    private static bool IsShortText(JToken text) => text switch
    {
        JValue { Type: JTokenType.String } value => ((string)value).Length <= MaxLabelLength,
        JObject translations => translations.Properties().All(x => x.Value is not JValue { Type: JTokenType.String } value || ((string)value).Length <= MaxLabelLength),
        _ => true,
    };

    /// <summary>The name a catalog is stored under: the lowercase hex SHA-256 of its UTF-8 text, so that no other text can take it.</summary>
    protected virtual string GetHash(string catalogJson) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(catalogJson)));

    [Obsolete("A catalog is stored under the SHA-256 of its text, whatever its hash property says. Use GetHash(string).")]
    protected virtual string GetHash(JObject catalog) => catalog[HashProperty] is JValue { Type: JTokenType.String } hash ? (string)hash : null;

    protected virtual string GetCatalogPath(string hash) => $"{ThemeStorage.CatalogsFolder}/{hash}.json";
}
