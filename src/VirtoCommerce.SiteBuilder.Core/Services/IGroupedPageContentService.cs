using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>Versioned authoring capability, separate from the existing grouped CRUD contract.</summary>
public interface IGroupedPageContentService
{
    Task<SiteBuilderContentSnapshot> LoadGroupContentAsync(
        GroupedSiteBuilderPage authorizedGroup, bool draft = true, CancellationToken cancellationToken = default);

    Task<SiteBuilderConditionalContentWriteResult> SaveGroupContentAsync(
        GroupedSiteBuilderPage authorizedGroup, string content, string expectedETag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the group as <see cref="IGroupedPageService"/> does, when the content of its draft, else of its published page (with
    /// <paramref name="draft"/> <c>false</c>, of its published page only), is still at <paramref name="expectedETag"/>, read under the lock of the save.
    /// </summary>
    /// <exception cref="SiteBuilderContentConflictException">It is at another version, or the group is gone: nothing is written.</exception>
    Task SaveGroupAtVersionAsync(GroupedSiteBuilderPage group, bool draft, string expectedETag, CancellationToken cancellationToken = default);

    Task SaveGroupSettingsAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken = default);

    /// <summary>Saves the settings as <see cref="SaveGroupSettingsAsync(GroupedSiteBuilderPage, CancellationToken)"/> does, when the condition holds under the lock of the save.</summary>
    /// <exception cref="SiteBuilderContentConflictException">It does not hold: nothing is written.</exception>
    Task SaveGroupSettingsAsync(GroupedSiteBuilderPage group, GroupedPageSaveCondition condition, CancellationToken cancellationToken = default);

    /// <summary>Saves the group as <see cref="IGroupedPageService"/> does, when the condition holds under the lock of the save.</summary>
    /// <exception cref="SiteBuilderContentConflictException">It does not hold: nothing is written.</exception>
    Task SaveGroupAsync(GroupedSiteBuilderPage group, GroupedPageSaveCondition condition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the drafts of the group, when the content of its draft is still at <paramref name="expectedETag"/>: compared and deleted in one
    /// transaction under the lock of the group, so that a save in between is never deleted with them.
    /// </summary>
    /// <returns>The ids of the pages deleted.</returns>
    /// <exception cref="SiteBuilderContentConflictException">It is at another version, there is no draft, or the group is gone: nothing is deleted.</exception>
    Task<IList<string>> DeleteDraftsAtVersionAsync(GroupedSiteBuilderPage group, string expectedETag, CancellationToken cancellationToken = default);
}
