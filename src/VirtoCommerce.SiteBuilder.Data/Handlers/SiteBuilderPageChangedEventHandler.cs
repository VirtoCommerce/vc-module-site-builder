using Microsoft.Extensions.Logging;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Data.Handlers;

public class SiteBuilderPageChangedEventHandler(
    IEventPublisher eventPublisher,
    IGroupedPageService groupedPageService,
    ISiteBuilderSharedComponentResolver sharedComponentResolver,
    ISiteBuilderAssetReferenceIndexService assetReferenceIndexService,
    ILogger<SiteBuilderPageChangedEventHandler> logger
) : SiteBuilderEventHandlerBase(groupedPageService, sharedComponentResolver, logger), IEventHandler<SiteBuilderPageChangedEvent>
{
    public async Task Handle(SiteBuilderPageChangedEvent message)
    {
        await UpdateReferenceIndex(message);

        var changedEntries = message.ChangedEntries.ToArray();
        var events = await SelectBoundedAsync(
            changedEntries,
            x =>
            {
                var page = x.NewEntry ?? x.OldEntry;
                return ToPagesDomainEvent(page, x.EntryState);
            });

        await PublishPagesDomainEvents(events, eventPublisher);
    }

    private async Task UpdateReferenceIndex(SiteBuilderPageChangedEvent message)
    {
        var deletedPageIds = message.ChangedEntries
            .Where(x => x.EntryState == EntryState.Deleted)
            .Select(x => x.OldEntry?.Id ?? x.NewEntry?.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        await assetReferenceIndexService.DeletePageIndexAsync(deletedPageIds);
    }
}
