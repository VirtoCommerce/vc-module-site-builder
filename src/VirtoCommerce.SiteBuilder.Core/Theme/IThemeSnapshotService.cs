namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemeSnapshotService
{
    /// <summary>Returns null when the store does not exist. The first read publishes the live files when nothing is published yet.</summary>
    /// <exception cref="ThemeValidationException">Nothing is published yet, and the live files fail the shape check.</exception>
    Task<ThemeSnapshot> GetPublishedAsync(string storeId, string cultureName, CancellationToken cancellationToken = default);
}
