using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;

namespace VirtoCommerce.SiteBuilder.Data.Extensions;

public static class SchemaCatalogServiceExtensions
{
    /// <summary>The catalog of the hash, the store's current one for no hash; null for none, and for one that is not a JSON object.</summary>
    public static async Task<JObject> GetCatalogObjectAsync(this ISchemaCatalogService schemaCatalogService, string storeId, string hash,
        CancellationToken cancellationToken = default)
    {
        var catalog = await schemaCatalogService.GetAsync(storeId, hash, cancellationToken);
        if (catalog == null)
        {
            return null;
        }

        try
        {
            return ThemeJson.ParseObject(catalog);
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    /// <summary>The catalog content is checked against: the one of the hash, else the store's current one; null when the store has none.</summary>
    public static async Task<JObject> GetCatalogOrCurrentAsync(this ISchemaCatalogService schemaCatalogService, string storeId, string hash,
        CancellationToken cancellationToken = default)
    {
        var catalog = string.IsNullOrEmpty(hash) ? null : await schemaCatalogService.GetCatalogObjectAsync(storeId, hash, cancellationToken);

        return catalog ?? await schemaCatalogService.GetCatalogObjectAsync(storeId, hash: null, cancellationToken);
    }
}
