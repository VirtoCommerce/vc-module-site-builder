using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface IGroupedPageContentWriter
{
    /// <exception cref="InvalidDataException">Something in the content can run script.</exception>
    Task<SiteBuilderConditionalContentWriteResult> SaveContentAsync(GroupedSiteBuilderPage group, string content, string expectedETag,
        CancellationToken cancellationToken = default);
}
