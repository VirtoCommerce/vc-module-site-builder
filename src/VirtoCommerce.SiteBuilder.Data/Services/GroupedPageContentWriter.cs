using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Data.Services;

/// <summary>Writes the content with its HTML sanitized, and raises the page's changed event when the content service did not.</summary>
public class GroupedPageContentWriter(
    IGroupedPageContentService groupedPageContentService,
    ISiteBuilderPageService pageService,
    IEventPublisher eventPublisher,
    ISiteBuilderContentGuard contentGuard) : IGroupedPageContentWriter
{
    public virtual async Task<SiteBuilderConditionalContentWriteResult> SaveContentAsync(GroupedSiteBuilderPage group, string content, string expectedETag,
        CancellationToken cancellationToken = default)
    {
        // before the write: the version returned is of what is stored
        content = contentGuard.GuardPageContent(content, "page", catalog: null);
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
