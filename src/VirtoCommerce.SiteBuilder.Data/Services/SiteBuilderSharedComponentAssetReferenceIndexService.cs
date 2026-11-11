using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public interface ISiteBuilderSharedComponentAssetReferenceIndexService
{
    Task RebuildIndexInCurrentUnitOfWorkAsync(
        ISiteBuilderRepository repository,
        string sharedComponentId,
        string content,
        CancellationToken cancellationToken = default);
}

public sealed class SiteBuilderSharedComponentAssetReferenceIndexService
    : ISiteBuilderSharedComponentAssetReferenceIndexService
{
    public Task RebuildIndexInCurrentUnitOfWorkAsync(
        ISiteBuilderRepository repository,
        string sharedComponentId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        return RebuildIndexInCurrentUnitOfWorkCoreAsync(
            repository,
            sharedComponentId,
            content,
            cancellationToken);
    }

    private static async Task RebuildIndexInCurrentUnitOfWorkCoreAsync(
        ISiteBuilderRepository repository,
        string sharedComponentId,
        string content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sharedComponentId))
        {
            return;
        }

        var existingReferences = await repository.SiteBuilderSharedComponentAssetReferences
            .Where(x => x.SharedComponentId == sharedComponentId)
            .ToListAsync(cancellationToken);

        foreach (var reference in existingReferences)
        {
            repository.Remove(reference);
        }

        foreach (var normalizedAssetUrl in SiteBuilderAssetReferenceMatcher.ExtractReferences(content))
        {
            repository.Add(new SiteBuilderSharedComponentAssetReferenceEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                SharedComponentId = sharedComponentId,
                NormalizedAssetUrl = normalizedAssetUrl,
                NormalizedAssetUrlHash = SiteBuilderAssetReferenceMatcher.GetAssetUrlHash(normalizedAssetUrl),
            });
        }
    }
}
