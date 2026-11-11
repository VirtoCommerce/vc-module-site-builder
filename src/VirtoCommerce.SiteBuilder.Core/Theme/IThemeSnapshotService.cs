namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemeSnapshotService
{
    /// <summary>Returns null when the new Site Builder is off for the store. The first read publishes the live files when nothing is published yet.</summary>
    /// <exception cref="ThemeValidationException">Nothing is published yet, and the live files fail the shape check.</exception>
    Task<ThemeSnapshot> GetPublishedAsync(string storeId, string cultureName, CancellationToken cancellationToken = default);

    Task<bool> IsNewSiteBuilderEnabledAsync(string storeId, CancellationToken cancellationToken = default);
}
