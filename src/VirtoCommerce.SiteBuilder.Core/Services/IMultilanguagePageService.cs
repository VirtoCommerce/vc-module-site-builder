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
    /// Saves the root as <see cref="UpdateAsync(MultilanguagePage, CancellationToken)"/> does, when it is still at <paramref name="expectedVersion"/>
    /// (<see cref="SiteBuilderContentVersion.CreateForMultilanguagePage"/>), read right before the save as the root has no lock of its own; and with
    /// <paramref name="requireUnpublished"/>, when no published language version gets other settings, as each save of one reads it under its lock.
    /// </summary>
    /// <param name="expectedVersion"><c>null</c> saves over whatever the root holds.</param>
    /// <exception cref="SiteBuilderContentConflictException">
    /// The root changed, or a published version would change. Before the root is saved nothing is written; a version published between that and
    /// its own save is refused there, after the root and the versions before it are saved.
    /// </exception>
    Task<MultilanguagePage> UpdateAsync(MultilanguagePage model, bool requireUnpublished, string expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes an existing page a language version of the root. Fields whose value differs from the root become overridden,
    /// so linking never changes how the page is served.
    /// </summary>
    Task<GroupedSiteBuilderPage> LinkAsync(string multilanguagePageId, string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links the page as <see cref="LinkAsync(string, string, CancellationToken)"/> does; with <paramref name="requireUnpublished"/>, only a page not
    /// published, as read under the lock of the link.
    /// </summary>
    /// <param name="expectedVersion">
    /// The version of the root (<see cref="SiteBuilderContentVersion.CreateForMultilanguagePage"/>), which has no lock of its own: read under the lock
    /// of the link, and again by the save after it once that has taken the overrides from it; <c>null</c> links to whatever it holds.
    /// </param>
    /// <param name="expectedPageVersion">
    /// The version of the page's settings (<see cref="SiteBuilderContentVersion.CreateForGroupSettings"/>), read under the lock of the link;
    /// <c>null</c> links the page whatever it holds.
    /// </param>
    /// <exception cref="SiteBuilderContentConflictException">
    /// The page is published, or the root or the page changed: it is not linked, or the link is undone when the save after it finds the root changed.
    /// </exception>
    Task<GroupedSiteBuilderPage> LinkAsync(string multilanguagePageId, string groupId, bool requireUnpublished, string expectedVersion, string expectedPageVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns a language version back into a Landing Page with its current values.
    /// </summary>
    Task<GroupedSiteBuilderPage> UnlinkAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unlinks the page as <see cref="UnlinkAsync(string, CancellationToken)"/> does; with <paramref name="requireUnpublished"/>, only a page not
    /// published, as read under the lock of the unlink.
    /// </summary>
    /// <param name="expectedVersion">
    /// The version of the page's settings (<see cref="SiteBuilderContentVersion.CreateForGroupSettings"/>), read under the lock of the unlink;
    /// <c>null</c> unlinks the page whatever it holds.
    /// </param>
    /// <exception cref="SiteBuilderContentConflictException">The page is published, or changed: it is not unlinked.</exception>
    Task<GroupedSiteBuilderPage> UnlinkAsync(string groupId, bool requireUnpublished, string expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns a Landing Page into a Multilanguage Page: a new root takes the page's name, permalink and settings,
    /// and the page becomes its default language version, so the storefront keeps serving it unchanged.
    /// </summary>
    Task<MultilanguagePage> ConvertAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts the page as <see cref="ConvertAsync(string, CancellationToken)"/> does; with <paramref name="requireUnpublished"/>, only a page not
    /// published, as read under the lock of its link.
    /// </summary>
    /// <param name="expectedVersion">
    /// The version of the page's settings (<see cref="SiteBuilderContentVersion.CreateForGroupSettings"/>), which the root takes, read before the
    /// root is created and again under the lock of the link; <c>null</c> converts the page whatever it holds.
    /// </param>
    /// <exception cref="SiteBuilderContentConflictException">The page is published, or changed: no root is left behind.</exception>
    Task<MultilanguagePage> ConvertAsync(string groupId, bool requireUnpublished, string expectedVersion, CancellationToken cancellationToken = default);

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
    /// Marks the translation as <see cref="MarkUpToDateAsync(string, CancellationToken)"/> does, when the source revision is still
    /// <paramref name="expectedSourceRevisionDate"/>, read under the lock of the save, which takes the source language version's lock too.
    /// </summary>
    /// <param name="expectedSourceRevisionDate">
    /// Matched within a millisecond, as a revision sent through JSON may lose a fraction of one; <c>null</c> takes the source revision as it is.
    /// </param>
    /// <exception cref="SiteBuilderContentConflictException">The source was published again: nothing is written.</exception>
    Task<GroupedSiteBuilderPage> MarkUpToDateAsync(string groupId, DateTime? expectedSourceRevisionDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the root values of the given fields to every language version, dropping their overrides of those fields.
    /// </summary>
    /// <returns>Languages whose versions changed.</returns>
    Task<IList<string>> ResetOverridesAsync(string multilanguagePageId, IList<string> fields, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the overrides as <see cref="ResetOverridesAsync(string, IList{string}, CancellationToken)"/> does; with <paramref name="requireUnpublished"/>,
    /// only when no published version changes, as each save of one reads it under its lock.
    /// </summary>
    /// <param name="expectedVersion">
    /// The version of the root whose values the versions take (<see cref="SiteBuilderContentVersion.CreateForMultilanguagePage"/>), which has no lock
    /// of its own: read right before the first save, and again by each save once that has taken the overrides from it; <c>null</c> resets to whatever
    /// it holds.
    /// </param>
    /// <param name="expectedPageVersions">
    /// The settings version (<see cref="SiteBuilderContentVersion.CreateForGroupSettings"/>) of every language version the reset changes, by its id,
    /// as <see cref="PreviewResetOverridesAsync"/> answers them, each read again under the lock of its save; <c>null</c> resets whatever the
    /// versions hold.
    /// </param>
    /// <exception cref="SiteBuilderContentConflictException">
    /// A published version would change, the root changed, or the versions the reset changes are not those expected: one changed, appeared or went.
    /// Before the first save nothing is written; a version published or changed, or a root changed, after that is refused at the version's own save,
    /// after the versions before it are reset.
    /// </exception>
    Task<IList<string>> ResetOverridesAsync(string multilanguagePageId, IList<string> fields, bool requireUnpublished, string expectedVersion,
        IDictionary<string, string> expectedPageVersions, CancellationToken cancellationToken = default);

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
    /// <param name="expectedSettingsVersions">
    /// The settings version (<see cref="SiteBuilderContentVersion.CreateForGroupSettings"/>) of every version with a draft, by its id; a version
    /// whose settings changed after the check, as read again before its save, is skipped with <see cref="MultilanguagePageBatchResult.ChangedReason"/>.
    /// <c>null</c> publishes the drafts whatever the settings hold.
    /// </param>
    /// <exception cref="SiteBuilderContentConflictException">A draft or the settings of a version with one changed, appeared or went: nothing is published.</exception>
    Task<MultilanguagePageBatchResult> PublishAllAsync(string multilanguagePageId, IDictionary<string, string> expectedDraftVersions,
        IDictionary<string, string> expectedSettingsVersions, CancellationToken cancellationToken = default);

    /// <summary>Archives every language version.</summary>
    Task<MultilanguagePageBatchResult> ArchiveAllAsync(string multilanguagePageId, CancellationToken cancellationToken = default);

    // Each preview checks the request as its write does, throwing the same exceptions, and answers what the write would change
    // over what is stored now, writing nothing.

    Task<SiteBuilderChangePreview> PreviewCreateAsync(MultilanguagePage model, CancellationToken cancellationToken = default);

    Task<SiteBuilderChangePreview> PreviewUpdateAsync(MultilanguagePage model, CancellationToken cancellationToken = default);

    /// <summary>The page, with the version of its settings; and, with the version a link compares, the root, whose fields do not change.</summary>
    Task<SiteBuilderChangePreview> PreviewLinkAsync(string multilanguagePageId, string groupId, CancellationToken cancellationToken = default);

    /// <summary>The page, with the version of its settings.</summary>
    Task<SiteBuilderChangePreview> PreviewUnlinkAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>The new root has no id yet, and neither has the link to it. The page comes with the version of its settings.</summary>
    Task<SiteBuilderChangePreview> PreviewConvertAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>The copies have no ids yet; their content is not compared.</summary>
    Task<SiteBuilderChangePreview> PreviewCloneAsync(string multilanguagePageId, CancellationToken cancellationToken = default);

    Task<SiteBuilderChangePreview> PreviewMarkUpToDateAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="PreviewMarkUpToDateAsync(string, CancellationToken)"/>, refusing a source revision that is not
    /// <paramref name="expectedSourceRevisionDate"/> as <see cref="MarkUpToDateAsync(string, DateTime?, CancellationToken)"/> does.
    /// </summary>
    Task<SiteBuilderChangePreview> PreviewMarkUpToDateAsync(string groupId, DateTime? expectedSourceRevisionDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// The versions the reset changes, each with the version of its settings; and, when there is one, the root with the version a reset compares,
    /// whose fields do not change.
    /// </summary>
    Task<SiteBuilderChangePreview> PreviewResetOverridesAsync(string multilanguagePageId, IList<string> fields, CancellationToken cancellationToken = default);

    /// <summary>The new version has no id yet; its content is not compared.</summary>
    Task<SiteBuilderChangePreview> PreviewCreateLanguageAsync(string multilanguagePageId, string cultureName, bool copyContent,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the versions whose draft would be published, each with the version of its draft; the others are skipped by the write too.</summary>
    Task<SiteBuilderChangePreview> PreviewPublishAllAsync(string multilanguagePageId, CancellationToken cancellationToken = default);
}
