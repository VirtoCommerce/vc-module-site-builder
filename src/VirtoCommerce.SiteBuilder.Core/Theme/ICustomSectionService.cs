using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

/// <summary>The custom section types of a theme, each as its draft holds it, else as it is live.</summary>
public interface ICustomSectionService
{
    /// <param name="themeName">The theme folder; <c>null</c> for the store's theme.</param>
    /// <returns>The type of each file in the folder, valid or not.</returns>
    Task<IList<string>> ListTypesAsync(string storeId, string themeName, CancellationToken cancellationToken = default);

    /// <returns><c>null</c> for no file, or one that is not JSON.</returns>
    Task<JToken> GetTypeAsync(string storeId, string themeName, string type, CancellationToken cancellationToken = default);

    /// <summary>By type; a file that is not JSON is left out.</summary>
    Task<IDictionary<string, JToken>> GetTypesAsync(string storeId, string themeName, CancellationToken cancellationToken = default);

    /// <summary>The catalog with the store theme's types in place of those the preview sent, which may not know one just written. Null for no catalog.</summary>
    /// <param name="files">Contents by the path with the theme folder, over the stored ones; those of another theme are left out.</param>
    Task<JObject> GetThemeCatalogAsync(string storeId, JObject catalog, IDictionary<string, JToken> files, CancellationToken cancellationToken = default);
}
