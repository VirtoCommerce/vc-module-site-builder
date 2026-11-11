using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.Platform.Caching;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.Jobs;

namespace VirtoCommerce.SiteBuilder.Data.Handlers;

public class SiteBuilderSharedComponentContentPropagationJobPayload
{
    public string[] SharedComponentIds { get; set; } = [];
}

public class SiteBuilderSharedComponentContentPropagationJob(
    ISiteBuilderSharedComponentReferenceIndexService referenceIndexService,
    ISiteBuilderPageService pageService,
    IEventPublisher eventPublisher)
    : IBackgroundJobHandler<SiteBuilderSharedComponentContentPropagationJobPayload>
{
    internal const int PageBatchSize = 500;

    /// <summary>
    /// Retries a failed propagation, as Hangfire's [AutomaticRetry(Attempts = 3)] did. The new job API takes the retry
    /// count per enqueue, so <see cref="SiteBuilderSharedComponentContentChangedEventHandler"/> passes it.
    /// </summary>
    public const int MaxRetryAttempts = 3;

    public virtual Task Execute(SiteBuilderSharedComponentContentPropagationJobPayload payload, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        return ProcessAsync(payload.SharedComponentIds, cancellationToken);
    }

    public async Task ProcessAsync(
        string[] sharedComponentIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pageIds = await referenceIndexService.GetPageIdsAsync(
            sharedComponentIds,
            cancellationToken);
        if (pageIds.Count == 0)
        {
            return;
        }

        // Page saves publish their own event, covering references that move around this snapshot.
        foreach (var pageIdBatch in BatchPageIds(pageIds))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var pageId in pageIdBatch)
            {
                GenericCachingRegion<SiteBuilderPage>.ExpireTokenForKey(pageId);
            }

            GenericSearchCachingRegion<SiteBuilderPage>.ExpireRegion();

            var pages = await pageService.GetAsync(pageIdBatch);
            var changedEntries = pages
                .Select(x => new GenericChangedEntry<SiteBuilderPage>(x, EntryState.Modified))
                .ToArray();

            if (changedEntries.Length > 0)
            {
                await eventPublisher.Publish(
                    new SiteBuilderPageChangedEvent(changedEntries),
                    cancellationToken);
            }
        }
    }

    internal static IEnumerable<string[]> BatchPageIds(IEnumerable<string> pageIds)
    {
        return pageIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x, StringComparer.Ordinal)
            .Chunk(PageBatchSize);
    }
}
