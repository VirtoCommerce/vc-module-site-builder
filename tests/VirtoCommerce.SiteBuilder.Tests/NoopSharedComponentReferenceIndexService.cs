using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.Tests;

internal class NoopSharedComponentReferenceIndexService : ISiteBuilderSharedComponentReferenceIndexService
{
    public virtual Task ValidateReferencesForStoreAsync(
        string storeId,
        IEnumerable<string> contents,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task<IList<string>> GetPageIdsAsync(
        IEnumerable<string> sharedComponentIds,
        CancellationToken cancellationToken = default) => Task.FromResult<IList<string>>([]);
}
