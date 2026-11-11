using Microsoft.Extensions.Logging;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Data.Handlers
{
    public class GroupedSiteBuilderPageChangedEventHandler(
        IEventPublisher eventPublisher,
        IGroupedPageService groupedPageService,
        ISiteBuilderSharedComponentResolver sharedComponentResolver,
        ISiteBuilderAssetReferenceIndexService assetReferenceIndexService,
        ILogger<GroupedSiteBuilderPageChangedEventHandler> logger
    ) : SiteBuilderEventHandlerBase(groupedPageService, sharedComponentResolver, logger), IEventHandler<GroupedSiteBuilderPageChangedEvent>
    {
        public async Task Handle(GroupedSiteBuilderPageChangedEvent message)
        {
            await UpdateReferenceIndex(message);

            var pageChanges = message.ChangedEntries
                .SelectMany(x =>
                {
                    var groupedEntry = x.NewEntry ?? x.OldEntry;
                    return groupedEntry.Pages.Select(page => (Page: page, x.EntryState));
                })
                .ToArray();

            var events = await SelectBoundedAsync(
                pageChanges,
                x => ToPagesDomainEvent(x.Page, x.EntryState));

            await PublishPagesDomainEvents(events, eventPublisher);
        }

        private async Task UpdateReferenceIndex(GroupedSiteBuilderPageChangedEvent message)
        {
            var deletedGroupIds = message.ChangedEntries
                .Where(x => x.EntryState == EntryState.Deleted)
                .Select(x => x.OldEntry?.Id ?? x.NewEntry?.Id)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            await assetReferenceIndexService.DeleteGroupIndexAsync(deletedGroupIds);
        }
    }
}
