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

    /// <summary>
    /// Saves the content unless the component was deleted or recreated since <paramref name="expectedComponent"/> was read: then <c>false</c>.
    /// <paramref name="expectedContentVersion"/> is the <see cref="SiteBuilderContentVersion.CreateForFileContent"/> of the content the caller
    /// reviewed, compared under the component's write lock; <c>null</c> writes over whatever the content holds.
    /// </summary>
    /// <exception cref="SiteBuilderContentConflictException">The content is at another version: nothing is written.</exception>
    Task<bool> TrySaveContentAsync(
        SiteBuilderSharedComponent expectedComponent,
        string content,
        string expectedContentVersion,
        CancellationToken cancellationToken = default);
}
