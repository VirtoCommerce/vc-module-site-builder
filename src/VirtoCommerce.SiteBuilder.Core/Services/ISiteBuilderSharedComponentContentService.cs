using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderSharedComponentContentService
{
    Task<string> LoadContentAsync(string sharedComponentId, CancellationToken cancellationToken = default);

    Task<string> TryLoadContentAsync(
        SiteBuilderSharedComponent expectedComponent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> LoadContentsAsync(
        IEnumerable<string> sharedComponentIds,
        CancellationToken cancellationToken = default);

    Task SaveContentAsync(
        string sharedComponentId,
        string content,
        CancellationToken cancellationToken = default);

    Task<bool> TrySaveContentAsync(
        SiteBuilderSharedComponent expectedComponent,
        string content,
        CancellationToken cancellationToken = default);
}
