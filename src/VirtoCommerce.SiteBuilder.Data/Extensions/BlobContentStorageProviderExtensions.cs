using VirtoCommerce.ContentModule.Core.Services;

namespace VirtoCommerce.SiteBuilder.Data.Extensions;

public static class BlobContentStorageProviderExtensions
{
    public static Task<string> ReadTextAsync(this IBlobContentStorageProvider storageProvider, string path)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);

        return ReadTextInternalAsync(storageProvider, path);
    }

    private static async Task<string> ReadTextInternalAsync(IBlobContentStorageProvider storageProvider, string path)
    {
        if (await storageProvider.GetBlobInfoAsync(path) == null)
        {
            return null;
        }

        await using var stream = await storageProvider.OpenReadAsync(path);
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync();
    }
}
