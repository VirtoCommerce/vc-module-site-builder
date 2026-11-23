using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

public class CustomSectionService(IThemeStorageFactory themeStorageFactory, IThemeDraftService themeDraftService) : ICustomSectionService
{
    public virtual async Task<IList<string>> ListTypesAsync(string storeId, string themeName, CancellationToken cancellationToken = default) =>
        await ThemeCustomSections.ListTypesAsync(themeStorageFactory.Open(storeId, themeName ?? await themeStorageFactory.GetThemeNameAsync(storeId)));

    public virtual async Task<IDictionary<string, JToken>> GetTypesAsync(string storeId, string themeName, IDictionary<string, JToken> files = null,
        ICollection<string> types = null, CancellationToken cancellationToken = default)
    {
        themeName ??= await themeStorageFactory.GetThemeNameAsync(storeId);
        var result = new Dictionary<string, JToken>(StringComparer.Ordinal);
        // a type is part of the path it is read from, so a text that is not one is not read
        foreach (var type in types?.Where(ThemeStorage.IsCustomSectionType) ?? await ListTypesAsync(storeId, themeName, cancellationToken))
        {
            var file = await themeDraftService.ReadAsync(storeId, $"{themeName}/{ThemeStorage.GetCustomSectionFile(type)}", cancellationToken);
            if (Parse(file.Content) is { } content)
            {
                result[type] = content;
            }
        }

        return files == null ? result : ThemeCustomSections.WithFiles(result, themeName, files);
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
