using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// Permalink conflicts of publishing a page, shared by publishing a single page and a Multilanguage Page batch.
/// </summary>
public interface ISiteBuilderPublishConflictService
{
    /// <summary>
    /// Other pages of the store that use the permalink of the page, with the conflict that publishing it creates.
    /// </summary>
    Task<IList<SiteBuilderPermalinkUsage>> FindPermalinkUsagesAsync(GroupedSiteBuilderPage page);

    /// <summary>
    /// Published pages that publishing the page would completely replace (Certain conflicts), and their groups as stored.
    /// </summary>
    Task<(IList<SiteBuilderPermalinkUsage> Conflicts, IList<GroupedSiteBuilderPage> Groups)> FindBlockingConflictsAsync(GroupedSiteBuilderPage page);

    /// <summary>
    /// Runs the conflict check and the save of a publish one at a time for pages with the store and permalink of the page.
    /// </summary>
    Task<T> ExecuteExclusiveAsync<T>(GroupedSiteBuilderPage page, Func<Task<T>> publish, CancellationToken cancellationToken = default);
}
