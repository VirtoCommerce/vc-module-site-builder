using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.Platform.Core.DistributedLock;
using VirtoCommerce.Platform.Core.Jobs;

namespace VirtoCommerce.SiteBuilder.Data.BackgroundJobs;

public class PagesMigrationJobPayload
{
}

/// <summary>
/// One-time migration of page metadata out of page content, enqueued at startup by
/// <see cref="PagesMigrationService.StartMigration"/> until it has completed once.
/// </summary>
public class PagesMigrationJob(PagesMigrationService migrationService)
    : IBackgroundJobHandler<PagesMigrationJobPayload>
{
    public virtual Task Execute(PagesMigrationJobPayload payload, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        return migrationService.MigratePages();
    }
}

public class AssetReferenceIndexRebuildJobPayload
{
}

/// <summary>
/// One-time build of the asset reference index for existing pages, enqueued at startup by
/// <see cref="SiteBuilderAssetReferenceMigrationService.StartMigration"/> until it has completed once.
/// </summary>
public class AssetReferenceIndexRebuildJob(SiteBuilderAssetReferenceMigrationService migrationService, IDistributedLock distributedLock)
    : IBackgroundJobHandler<AssetReferenceIndexRebuildJobPayload>
{
    // Replaces Hangfire's [DisableConcurrentExecution(24 hours)]: one rebuild at a time across the worker fleet, since
    // every instance enqueues one at startup until the index is marked migrated. A queued rebuild waits up to the
    // timeout and then fails so the engine retries it; once it runs, it sees the flag the first rebuild set and returns.
    private const string LockResource = "sitebuilder:job:rebuild-asset-reference-index";
    private static readonly TimeSpan _lockTimeout = TimeSpan.FromHours(24);

    public virtual Task Execute(AssetReferenceIndexRebuildJobPayload payload, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        return distributedLock.ExecuteAsync(LockResource, _ => migrationService.RebuildAssetReferenceIndex(), _lockTimeout, cancellationToken);
    }
}

public class PageBuilderSettingsCopyJobPayload
{
}

public class PageBuilderSettingsCopyJob(PageBuilderSettingsMigrationService migrationService, IDistributedLock distributedLock)
    : IBackgroundJobHandler<PageBuilderSettingsCopyJobPayload>
{
    // Every instance enqueues a copy at startup until the flag is set; the lock makes a queued one wait for the
    // running one and then find the flag set, rather than both checking for Site Builder rows at the same time.
    private const string LockResource = "sitebuilder:job:copy-page-builder-settings";
    private static readonly TimeSpan _lockTimeout = TimeSpan.FromHours(1);

    public virtual Task Execute(PageBuilderSettingsCopyJobPayload payload, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        return distributedLock.ExecuteAsync(LockResource, _ => migrationService.CopySettingsAsync(cancellationToken), _lockTimeout, cancellationToken);
    }
}
