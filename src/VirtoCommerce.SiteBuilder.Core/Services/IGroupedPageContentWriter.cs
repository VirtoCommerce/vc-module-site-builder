using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface IGroupedPageContentWriter
{
    Task<SiteBuilderConditionalContentWriteResult> SaveContentAsync(GroupedSiteBuilderPage group, string content, string expectedETag,
        CancellationToken cancellationToken = default);
}
