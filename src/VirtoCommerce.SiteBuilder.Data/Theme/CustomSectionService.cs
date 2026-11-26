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
        var listed = await ListTypesAsync(storeId, themeName, cancellationToken);
        // only the folder's own types are read, so what a request names costs no read of a type the theme does not hold
        foreach (var type in types == null ? listed : listed.Intersect(types, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await themeDraftService.ReadAsync(storeId, $"{themeName}/{ThemeStorage.GetCustomSectionFile(type)}", cancellationToken);
            if (TryParse(file.Content, out var content))
            {
                result[type] = content;
            }
        }

        return files == null ? result : ThemeCustomSections.WithFiles(result, themeName, files);
    }

    private static bool TryParse(string content, out JToken document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            document = ThemeJson.Parse(content);
            return true;
        }
        catch (JsonReaderException)
        {
            return false;
        }
    }
}
