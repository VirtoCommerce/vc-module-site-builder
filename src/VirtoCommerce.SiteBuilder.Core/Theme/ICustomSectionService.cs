using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

/// <summary>The custom section types of a theme, each as its draft holds it, else as it is live.</summary>
public interface ICustomSectionService
{
    /// <param name="themeName">The theme folder; <c>null</c> for the store's theme.</param>
    /// <returns>The type of each file in the folder, valid or not.</returns>
    Task<IList<string>> ListTypesAsync(string storeId, string themeName, CancellationToken cancellationToken = default);

    /// <summary>By type; a file that is not JSON is left out.</summary>
    /// <param name="themeName">The theme folder; <c>null</c> for the store's theme.</param>
    /// <param name="files">Contents by the path with the theme folder, over the stored ones, such as those a save writes; those of another theme are left out.</param>
    /// <param name="types">The stored types to read, such as those a save places; <c>null</c> for every type in the folder, as their size together needs.</param>
    Task<IDictionary<string, JToken>> GetTypesAsync(string storeId, string themeName, IDictionary<string, JToken> files = null, ICollection<string> types = null,
        CancellationToken cancellationToken = default);
}
