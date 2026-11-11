using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.GenericCrud;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderSharedComponentService : ICrudService<SiteBuilderSharedComponent>
{
    Task<SiteBuilderSharedComponent> UpdateMetadataAsync(
        SiteBuilderSharedComponent model,
        CancellationToken cancellationToken = default);

    Task<bool> TryDeleteAsync(
        SiteBuilderSharedComponent expectedComponent,
        CancellationToken cancellationToken = default);

    Task SaveWithContentAsync(
        SiteBuilderSharedComponent model,
        string content,
        CancellationToken cancellationToken = default);
}
