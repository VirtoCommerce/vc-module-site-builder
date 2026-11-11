using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VirtoCommerce.SiteBuilder.Data.GitContent;

namespace VirtoCommerce.SiteBuilder.Tests;

internal sealed class FakeGitContentOwnership(string storeId = null) : GitHubContentOwnership(null, null, null)
{
    public List<string> Asked { get; } = [];

    public List<string> Forgotten { get; } = [];

    public override Task<string> GetStoreIdAsync(string repository, CancellationToken cancellationToken = default)
    {
        Asked.Add(repository);
        return Task.FromResult(storeId);
    }

    public override void Invalidate(string repository) => Forgotten.Add(repository);
}
