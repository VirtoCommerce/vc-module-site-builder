using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

public class CustomSectionService(IThemeStorageFactory themeStorageFactory, IThemeDraftService themeDraftService) : ICustomSectionService
{
    public virtual async Task<IList<string>> ListTypesAsync(string storeId, string themeName, CancellationToken cancellationToken = default)
    {
        var storage = themeStorageFactory.Open(storeId, themeName ?? await themeStorageFactory.GetThemeNameAsync(storeId));

        return await ListTypesAsync(storage);
    }

    public virtual async Task<JToken> GetTypeAsync(string storeId, string themeName, string type, CancellationToken cancellationToken = default)
    {
        var path = $"{themeName ?? await themeStorageFactory.GetThemeNameAsync(storeId)}/{ThemeStorage.GetCustomSectionFile(type)}";

        return Parse((await themeDraftService.ReadAsync(storeId, path, cancellationToken)).Content);
    }

    public virtual async Task<IDictionary<string, JToken>> GetTypesAsync(string storeId, string themeName, CancellationToken cancellationToken = default)
    {
        themeName ??= await themeStorageFactory.GetThemeNameAsync(storeId);
        var result = new Dictionary<string, JToken>(StringComparer.Ordinal);
        foreach (var type in await ListTypesAsync(storeId, themeName, cancellationToken))
        {
            if (await GetTypeAsync(storeId, themeName, type, cancellationToken) is { } content)
            {
                result[type] = content;
            }
        }

        return result;
    }

    public virtual async Task<JObject> GetThemeCatalogAsync(string storeId, JObject catalog, IDictionary<string, JToken> files,
        CancellationToken cancellationToken = default)
    {
        if (catalog == null)
        {
            return null;
        }

        var themeName = await themeStorageFactory.GetThemeNameAsync(storeId);
        var types = await GetTypesAsync(storeId, themeName, cancellationToken);
        foreach (var (path, content) in files ?? new Dictionary<string, JToken>())
        {
            if (string.Equals(SiteBuilderContentPaths.GetThemeName(path), themeName, StringComparison.OrdinalIgnoreCase) &&
                ThemeCustomSections.GetCustomSectionType(SiteBuilderContentPaths.GetPathInTheme(path)) is { } type)
            {
                types[type] = content;
            }
        }

        return ThemeCustomSections.AddToCatalog(catalog, types);
    }

    // a draft listed by its draft file names the same type as its live file
    protected virtual async Task<IList<string>> ListTypesAsync(IThemeStorage storage)
    {
        var fileNames = await storage.ListFilesAsync(ThemeStorage.CustomSectionsFolder);

        return fileNames
            .Select(x => ThemeStorage.GetCustomSectionType(storage.GetLivePath(x)))
            .Where(x => x != null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
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
