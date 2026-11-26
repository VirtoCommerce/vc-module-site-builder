namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface ISchemaCatalogService
{
    /// <summary>Stores the catalog under the SHA-256 of its text and makes it the store's current catalog. Returns that hash.</summary>
    Task<string> SaveAsync(string storeId, string catalogJson, CancellationToken cancellationToken = default);

    /// <summary>The current catalog when the hash is empty.</summary>
    Task<string> GetAsync(string storeId, string hash, CancellationToken cancellationToken = default);

    /// <summary>The hash of the store's current catalog; null when it has none.</summary>
    Task<string> GetCurrentHashAsync(string storeId, CancellationToken cancellationToken = default);
}
