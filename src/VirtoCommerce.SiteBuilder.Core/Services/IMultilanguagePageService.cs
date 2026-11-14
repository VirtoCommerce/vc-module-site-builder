using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.GenericCrud;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// Saving a root copies its shared name and inherited settings into the linked language versions.
/// Deleting a root detaches its language versions; they stay as Landing Pages with their current values.
/// </summary>
public interface IMultilanguagePageService : ICrudService<MultilanguagePage>
{
    /// <summary>
    /// Saves a new root. The default language must be a store language and is stored as the store spells it;
    /// without one the root takes the store default language.
    /// </summary>
    Task<MultilanguagePage> CreateAsync(MultilanguagePage model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an existing root in its own store. A new default language must be a store language with a version.
    /// </summary>
    Task<MultilanguagePage> UpdateAsync(MultilanguagePage model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes an existing page a language version of the root. Fields whose value differs from the root become overridden,
    /// so linking never changes how the page is served.
    /// </summary>
    Task<GroupedSiteBuilderPage> LinkAsync(string multilanguagePageId, string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns a language version back into a Landing Page with its current values.
    /// </summary>
    Task<GroupedSiteBuilderPage> UnlinkAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns a Landing Page into a Multilanguage Page: a new root takes the page's name, permalink and settings,
    /// and the page becomes its default language version, so the storefront keeps serving it unchanged.
    /// </summary>
    Task<MultilanguagePage> ConvertAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the Multilanguage Page with every language version; versions start as drafts with the current content.
    /// Name and permalinks get the first copy suffix no page of the store uses yet ("-copy", "-copy-2"...),
    /// so the copy takes over neither the original's addresses nor those of an earlier copy.
    /// </summary>
    Task<MultilanguagePage> CloneAsync(string multilanguagePageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a translation as matching the current source language publication, e.g. when the source change needs no translation.
    /// </summary>
    Task<GroupedSiteBuilderPage> MarkUpToDateAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the root values of the given fields to every language version, dropping their overrides of those fields.
    /// </summary>
    /// <returns>Languages whose versions changed.</returns>
    Task<IList<string>> ResetOverridesAsync(string multilanguagePageId, IList<string> fields, CancellationToken cancellationToken = default);

    /// <summary>
    /// Language table: every store language (default language first) with its version status,
    /// followed by versions whose language was removed from the store.
    /// </summary>
    Task<IList<MultilanguagePageLanguage>> GetLanguagesAsync(string multilanguagePageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a draft version for a missing store language. With <paramref name="copyContent"/> the draft starts from the
    /// content of the default language version.
    /// </summary>
    Task<GroupedSiteBuilderPage> CreateLanguageAsync(string multilanguagePageId, string cultureName, bool copyContent, CancellationToken cancellationToken = default);

    /// <summary>Publishes the draft of every language version that has one.</summary>
    Task<MultilanguagePageBatchResult> PublishAllAsync(string multilanguagePageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes the draft of every language version that has one, when the drafts are those reviewed: the version of each, by the id of its
    /// language version, as <see cref="PreviewPublishAllAsync"/> answers them. A draft compared again under the lock of its save that has changed
    /// since is skipped with <see cref="MultilanguagePageBatchResult.ChangedReason"/>.
    /// </summary>
    /// <param name="expectedDraftVersions"><c>null</c> publishes the drafts whatever they hold.</param>
    /// <exception cref="SiteBuilderContentConflictException">A draft changed, appeared or went: nothing is published.</exception>
    Task<MultilanguagePageBatchResult> PublishAllAsync(string multilanguagePageId, IDictionary<string, string> expectedDraftVersions,
        CancellationToken cancellationToken = default);

    /// <summary>Archives every language version.</summary>
    Task<MultilanguagePageBatchResult> ArchiveAllAsync(string multilanguagePageId, CancellationToken cancellationToken = default);

    // Each preview checks the request as its write does, throwing the same exceptions, and answers what the write would change
    // over what is stored now, writing nothing.

    Task<SiteBuilderChangePreview> PreviewCreateAsync(MultilanguagePage model, CancellationToken cancellationToken = default);

    Task<SiteBuilderChangePreview> PreviewUpdateAsync(MultilanguagePage model, CancellationToken cancellationToken = default);

    Task<SiteBuilderChangePreview> PreviewLinkAsync(string multilanguagePageId, string groupId, CancellationToken cancellationToken = default);

    Task<SiteBuilderChangePreview> PreviewUnlinkAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>The new root has no id yet, and neither has the link to it.</summary>
    Task<SiteBuilderChangePreview> PreviewConvertAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>The copies have no ids yet; their content is not compared.</summary>
    Task<SiteBuilderChangePreview> PreviewCloneAsync(string multilanguagePageId, CancellationToken cancellationToken = default);

    Task<SiteBuilderChangePreview> PreviewMarkUpToDateAsync(string groupId, CancellationToken cancellationToken = default);

    Task<SiteBuilderChangePreview> PreviewResetOverridesAsync(string multilanguagePageId, IList<string> fields, CancellationToken cancellationToken = default);

    /// <summary>The new version has no id yet; its content is not compared.</summary>
    Task<SiteBuilderChangePreview> PreviewCreateLanguageAsync(string multilanguagePageId, string cultureName, bool copyContent,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the versions whose draft would be published, each with the version of its draft; the others are skipped by the write too.</summary>
    Task<SiteBuilderChangePreview> PreviewPublishAllAsync(string multilanguagePageId, CancellationToken cancellationToken = default);
}
