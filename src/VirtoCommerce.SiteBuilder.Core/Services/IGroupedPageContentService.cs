using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>Versioned authoring capability, separate from the existing grouped CRUD contract.</summary>
public interface IGroupedPageContentService
{
    Task<SiteBuilderContentSnapshot> LoadGroupContentAsync(
        GroupedSiteBuilderPage authorizedGroup, bool draft = true, CancellationToken cancellationToken = default);

    Task<SiteBuilderConditionalContentWriteResult> SaveGroupContentAsync(
        GroupedSiteBuilderPage authorizedGroup, string content, string expectedETag, CancellationToken cancellationToken = default);

    Task SaveGroupSettingsAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken = default);
}
