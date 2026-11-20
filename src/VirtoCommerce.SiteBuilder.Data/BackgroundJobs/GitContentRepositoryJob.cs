using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.Platform.Core.Jobs;

namespace VirtoCommerce.SiteBuilder.Data.BackgroundJobs;

public class GitContentRepositoryJobPayload
{
    public string StoreId { get; set; }

    public GitContentRepositoryAction Action { get; set; }

    public string Repository { get; set; }
}

public class GitContentRepositoryJob(IGitContentProvisioner gitContentProvisioner) : IBackgroundJobHandler<GitContentRepositoryJobPayload>
{
    public const int MaxRetryAttempts = 3;

    public virtual Task Execute(GitContentRepositoryJobPayload payload, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        return payload.Action == GitContentRepositoryAction.Archive
            ? gitContentProvisioner.ArchiveRepositoryAsync(payload.StoreId, payload.Repository, cancellationToken)
            : gitContentProvisioner.ConnectAsync(payload.StoreId, cancellationToken);
    }
}
