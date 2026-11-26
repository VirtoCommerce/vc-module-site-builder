using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

public class ThemeStorage(IBlobContentStorageProvider provider, IPublishingService publishingService, string themeName) : IThemeStorage
{
    public const string TemplatesFolder = "templates";
    public const string TemplateRulesFile = "config/template_rules.json";
    public const string HeaderGroupFile = "sections/header-group.json";
    public const string FooterGroupFile = "sections/footer-group.json";
    public const string BrandFile = "config/brand.json";
    public const string SettingsDataFile = "config/settings_data.json";
    public const string PaletteFile = "config/palette.json";
    public const string DarkPaletteFile = "config/palette.dark.json";
    public const string CustomSectionsFolder = "custom-sections";
    public const string SnapshotsFolder = "snapshots";
    public const string CurrentPublicationFile = SnapshotsFolder + "/current.json";
    public const string PublicationRecordFileName = "publication.json";
    public const string CatalogsFolder = "catalogs";
    public const string CurrentCatalogFile = CatalogsFolder + "/current.json";
    public const int CustomSectionTypeMaxLength = 64;
    public const string CustomSectionTypePrefix = "custom-";
    public const string CustomSectionTypePattern = $"^{CustomSectionTypeName}$";

    private const string CustomSectionTypeName = $"{CustomSectionTypePrefix}[a-z0-9]+(?:-[a-z0-9]+)*";
    private const string FolderType = "folder";
    private const string JsonExtension = ".json";

    private static readonly Regex TemplateKeyPattern = new("\\A(product|category|page)(\\.[a-z0-9]+(?:-[a-z0-9]+)*)?\\z", RegexOptions.None, TimeSpan.FromSeconds(1));

    // .NET's $ also matches before a final line feed
    private static readonly Regex CustomSectionTypeRegex = new($"\\A{CustomSectionTypeName}\\z", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
    };

    /// <summary>
    /// The theme's files other than its templates and custom section types. With those, they are all the status lists and a publication reads,
    /// so <see cref="IsThemeFile"/> is the whole set the builder saves: a publisher that reads another file names it here.
    /// </summary>
    public static IReadOnlyList<string> PartFiles { get; } =
        [TemplateRulesFile, HeaderGroupFile, FooterGroupFile, BrandFile, SettingsDataFile, PaletteFile, DarkPaletteFile];

    public string ThemeName { get; } = themeName;

    /// <summary>
    /// A file the theme's status lists and a publication reads, by the name they list it under: a part, a template or a custom section type,
    /// valid or not; only such a file is saved, restored or discarded by the builder.
    /// </summary>
    /// <param name="file">The path relative to the theme folder.</param>
    public static bool IsThemeFile(string file) =>
        file != null && (PartFiles.Contains(file, StringComparer.Ordinal) || IsTemplateFile(file) || IsCustomSectionFile(file));

    public static string GetLockResource(string storeId) => $"SiteBuilder:Theme:{storeId}";

    /// <summary>The lock's token is cancelled by either a lost lock or a cancelled request.</summary>
    public static bool IsLockLost(CancellationToken lockToken, CancellationToken requestToken) =>
        lockToken.IsCancellationRequested && !requestToken.IsCancellationRequested;

    public static bool IsTemplateKey(string key) => TemplateKeyPattern.IsMatch(key);

    public static string GetTemplateFile(string key) => $"{TemplatesFolder}/{key}{JsonExtension}";

    /// <summary>Returns null for a file name that does not name a template.</summary>
    public static string GetTemplateKey(string fileName)
    {
        if (!fileName.EndsWith(JsonExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = fileName[..^JsonExtension.Length];

        return IsTemplateKey(key) ? key : null;
    }

    public static bool IsCustomSectionType(string type) =>
        type.Length <= CustomSectionTypeMaxLength && CustomSectionTypeRegex.IsMatch(type);

    public static string GetCustomSectionFile(string type) => $"{CustomSectionsFolder}/{type}{JsonExtension}";

    /// <summary>The file name without .json, valid type or not; null for a file that is not JSON.</summary>
    public static string GetCustomSectionType(string fileName) =>
        fileName.EndsWith(JsonExtension, StringComparison.OrdinalIgnoreCase) ? fileName[..^JsonExtension.Length] : null;

    public virtual string GetPath(string file) => $"{ThemeName}/{file}";

    public virtual string GetDraftPath(string file) => publishingService.GetRelativeDraftUrl(file, true);

    public virtual string GetLivePath(string draftFile) => publishingService.GetRelativeDraftUrl(draftFile, false);

    public virtual async Task<bool> ExistsAsync(string file)
    {
        return await provider.GetBlobInfoAsync(GetPath(file)) != null;
    }

    public virtual async Task<string> ReadAsync(string file)
    {
        if (!await ExistsAsync(file))
        {
            return null;
        }

        await using var stream = await provider.OpenReadAsync(GetPath(file));
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync();
    }

    public virtual async Task WriteAsync(string file, string content)
    {
        await using var stream = await provider.OpenWriteAsync(GetPath(file));
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }

    public virtual async Task<T> ReadJsonAsync<T>(string file) where T : class
    {
        return DeserializeJson<T>(await ReadAsync(file));
    }

    public virtual Task WriteJsonAsync<T>(string file, T value) where T : class
    {
        return WriteAsync(file, JsonConvert.SerializeObject(value, SerializerSettings));
    }

    public virtual Task RemoveAsync(IEnumerable<string> files)
    {
        return provider.RemoveAsync(files.Select(GetPath).ToArray());
    }

    public virtual async Task<IList<string>> ListFilesAsync(string folder)
    {
        var result = await provider.SearchAsync(GetPath(folder), null);

        return result.Results.Where(x => x.Type != FolderType).Select(x => x.Name).ToList();
    }

    public virtual async Task<int?> GetCurrentPublicationAsync()
    {
        var pointer = await ReadAsync(CurrentPublicationFile);
        if (pointer == null)
        {
            return null;
        }

        if (GetPublication(DeserializeJson<ThemePublicationPointer>(pointer)) is { } publication)
        {
            return publication;
        }

        foreach (var candidate in (await GetPublicationsAsync()).OrderDescending())
        {
            if (await GetPublicationRecordAsync(candidate) != null)
            {
                return candidate;
            }
        }

        return null;
    }

    public virtual Task SetCurrentPublicationAsync(int publication)
    {
        var pointer = AbstractTypeFactory<ThemePublicationPointer>.TryCreateInstance();
        pointer.Publication = publication;

        return WriteJsonAsync(CurrentPublicationFile, pointer);
    }

    public virtual async Task<int> GetNextPublicationAsync()
    {
        var publications = await GetPublicationsAsync();
        var current = GetPublication(await ReadJsonAsync<ThemePublicationPointer>(CurrentPublicationFile)) ?? 0;

        return publications.Append(current).Max() + 1;
    }

    public virtual async Task<ThemePublicationRecord> GetPublicationRecordAsync(int publication)
    {
        var record = await ReadJsonAsync<ThemePublicationRecord>(GetPublicationRecordPath(publication));

        return string.IsNullOrEmpty(record?.DefaultLanguage) ? null : record;
    }

    public virtual Task WritePublicationRecordAsync(int publication, ThemePublicationRecord record)
    {
        return WriteJsonAsync(GetPublicationRecordPath(publication), record);
    }

    public virtual Task<string> ReadSnapshotAsync(int publication, string language)
    {
        return ReadAsync(GetSnapshotPath(publication, language));
    }

    public virtual Task WriteSnapshotAsync(int publication, string language, string content)
    {
        return WriteAsync(GetSnapshotPath(publication, language), content);
    }

    public virtual async Task RemovePublicationAsync(int publication)
    {
        var folder = GetPublicationFolder(publication);
        var files = await ListFilesAsync(folder);

        // exact files only: removing a folder by its path also takes "220" with "22" on some blob providers
        await RemoveAsync(files.OrderBy(x => x != PublicationRecordFileName).Select(x => $"{folder}/{x}"));
    }

    protected virtual string GetPublicationFolder(int publication) =>
        $"{SnapshotsFolder}/{publication.ToString(CultureInfo.InvariantCulture)}";

    protected virtual string GetSnapshotPath(int publication, string language) => $"{GetPublicationFolder(publication)}/{language}.json";

    protected virtual string GetPublicationRecordPath(int publication) => $"{GetPublicationFolder(publication)}/{PublicationRecordFileName}";

    protected virtual async Task<IList<string>> ListFoldersAsync(string folder)
    {
        var result = await provider.SearchAsync(GetPath(folder), null);

        return result.Results.Where(x => x.Type == FolderType).Select(x => x.Name).ToList();
    }

    protected virtual async Task<IList<int>> GetPublicationsAsync()
    {
        var folders = await ListFoldersAsync(SnapshotsFolder);

        return folders
            .Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var publication) ? publication : 0)
            .Where(x => x > 0)
            .ToList();
    }

    protected virtual T DeserializeJson<T>(string content) where T : class
    {
        if (content == null)
        {
            return null;
        }

        try
        {
            var result = AbstractTypeFactory<T>.TryCreateInstance();
            JsonConvert.PopulateObject(content, result, SerializerSettings);

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTemplateFile(string file) =>
        GetFileName(file, TemplatesFolder) is { } fileName && GetTemplateKey(fileName) is { } key && GetTemplateFile(key) == file;

    private static bool IsCustomSectionFile(string file) =>
        GetFileName(file, CustomSectionsFolder) is { } fileName && GetCustomSectionType(fileName) is { } type && GetCustomSectionFile(type) == file;

    /// <summary>The name of a file right in the folder, as the folder's listing gives it; null for a file anywhere else.</summary>
    private static string GetFileName(string file, string folder)
    {
        var prefix = folder + "/";

        return file.StartsWith(prefix, StringComparison.Ordinal) && file.IndexOf('/', prefix.Length) < 0 ? file[prefix.Length..] : null;
    }

    private static int? GetPublication(ThemePublicationPointer pointer) => pointer?.Publication > 0 ? pointer.Publication : null;
}
