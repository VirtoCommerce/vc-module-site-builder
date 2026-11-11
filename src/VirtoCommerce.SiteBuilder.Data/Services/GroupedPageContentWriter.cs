using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Data.Services;

/// <summary>Raises the page's changed event when the content service did not.</summary>
public class GroupedPageContentWriter(
    IGroupedPageContentService groupedPageContentService,
    ISiteBuilderPageService pageService,
    IEventPublisher eventPublisher) : IGroupedPageContentWriter
{
    public virtual async Task<SiteBuilderConditionalContentWriteResult> SaveContentAsync(GroupedSiteBuilderPage group, string content, string expectedETag,
        CancellationToken cancellationToken = default)
    {
        var result = await groupedPageContentService.SaveGroupContentAsync(group, content, expectedETag, cancellationToken);
        if (result.ContentWritten && !result.GroupedEventsPublished)
        {
            await PublishPageChangedEventAsync(result.PageId, cancellationToken);
        }

        return result;
    }

    protected virtual async Task PublishPageChangedEventAsync(string pageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(pageId))
        {
            return;
        }

        var page = (await pageService.GetAsync([pageId])).FirstOrDefault();
        if (page == null)
        {
            return;
        }

        await eventPublisher.Publish(new SiteBuilderPageChangedEvent([new GenericChangedEntry<SiteBuilderPage>(page, EntryState.Modified)]), cancellationToken);
    }
}
