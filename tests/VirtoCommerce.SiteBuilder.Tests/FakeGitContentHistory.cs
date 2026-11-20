using System.Threading;
using System.Threading.Tasks;
using VirtoCommerce.SiteBuilder.Core.GitContent;

namespace VirtoCommerce.SiteBuilder.Tests;

internal sealed class FakeGitContentHistory(GitPageHistory history = null) : IGitContentHistory
{
    public Task<GitPageHistory> GetHistoryAsync(string repository, string repoPath, GitHistoryQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(history ?? new GitPageHistory());

    public void Invalidate(string repository, string repoPath) { }
}
