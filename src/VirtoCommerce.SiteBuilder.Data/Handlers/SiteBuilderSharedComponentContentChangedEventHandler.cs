using Microsoft.Extensions.Logging;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.Jobs;

namespace VirtoCommerce.SiteBuilder.Data.Handlers;

public class SiteBuilderSharedComponentContentChangedEventHandler(
    ILogger<SiteBuilderSharedComponentContentChangedEventHandler> logger)
    : IEventHandler<SiteBuilderSharedComponentContentChangedEvent>
{
    private static readonly EnqueueOptions _enqueueOptions = new()
    {
        MaxRetryAttempts = SiteBuilderSharedComponentContentPropagationJob.MaxRetryAttempts,
    };

    public async Task Handle(SiteBuilderSharedComponentContentChangedEvent message)
    {
        var sharedComponentIds = message.SharedComponentIds.ToArray();
        if (sharedComponentIds.Length == 0)
        {
            return;
        }

        try
        {
            var payload = AbstractTypeFactory<SiteBuilderSharedComponentContentPropagationJobPayload>.TryCreateInstance();
            payload.SharedComponentIds = sharedComponentIds;

            // The static facade, not an injected IBackgroundJob: RegisterEventHandler resolves this handler once from
            // the root provider and holds it for the process lifetime, so it must not capture a Scoped dependency.
            await BackgroundJob.Enqueue<SiteBuilderSharedComponentContentPropagationJob>(payload, _enqueueOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to enqueue Shared Component propagation for {SharedComponentIds}",
                sharedComponentIds);
        }
    }
}
