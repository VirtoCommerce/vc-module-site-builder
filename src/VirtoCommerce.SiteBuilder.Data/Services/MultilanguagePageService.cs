using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.Platform.Caching;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.DistributedLock;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.GenericCrud;
using VirtoCommerce.Platform.Data.GenericCrud;
using VirtoCommerce.StoreModule.Core.Services;

namespace VirtoCommerce.SiteBuilder.Data.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class MultilanguagePageService(
    Func<ISiteBuilderRepository> repositoryFactory,
    IPlatformMemoryCache platformMemoryCache,
    IEventPublisher eventPublisher,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService groupedPageContentService,
    IGroupedPageSearchService groupedPageSearchService,
    IStoreService storeService,
    ISiteBuilderPageService pageService,
    IDistributedLock distributedLock,
    ISiteBuilderPublishConflictService publishConflictService,
    ILogger<MultilanguagePageService> logger)
    : CrudService<MultilanguagePage, MultilanguagePageEntity, MultilanguagePageChangingEvent, MultilanguagePageChangedEvent>(
        repositoryFactory, platformMemoryCache, eventPublisher),
      IMultilanguagePageService
{
    // What a save of the root asks of the saves of its language versions that follow it in AfterSaveChangesAsync, for the call that set it.
    private static readonly AsyncLocal<GroupedPageSaveCondition> LanguageVersionSaveCondition = new();

    protected override Task<IList<MultilanguagePageEntity>> LoadEntities(IRepository repository, IList<string> ids, string responseGroup)
    {
        return ((ISiteBuilderRepository)repository).GetMultilanguagePagesByIdsAsync(ids, responseGroup);
    }

    protected override async Task BeforeSaveChanges(IList<MultilanguagePage> models)
    {
        foreach (var model in models)
        {
            await PrepareSaveAsync(model);
        }

        await base.BeforeSaveChanges(models);
    }

    /// <summary>Gives the root what every save needs: a store and a source language.</summary>
    protected virtual async Task PrepareSaveAsync(MultilanguagePage model)
    {
        if (string.IsNullOrWhiteSpace(model.StoreId))
        {
            throw new InvalidOperationException("Multilanguage page must belong to a store.");
        }

        if (string.IsNullOrWhiteSpace(model.DefaultCultureName) && !string.IsNullOrEmpty(model.Id))
        {
            // A client that does not send the source language keeps it.
            model.DefaultCultureName = (await this.GetNoCloneAsync(model.Id))?.DefaultCultureName;
        }

        if (string.IsNullOrWhiteSpace(model.DefaultCultureName))
        {
            var store = await storeService.GetByIdAsync(model.StoreId)
                ?? throw new InvalidOperationException($"Store '{model.StoreId}' not found.");
            model.DefaultCultureName = store.DefaultLanguage;
        }
    }

    protected override async Task AfterSaveChangesAsync(IList<MultilanguagePage> models, IList<GenericChangedEntry<MultilanguagePage>> changedEntries)
    {
        await base.AfterSaveChangesAsync(models, changedEntries);

        var condition = LanguageVersionSaveCondition.Value;
        foreach (var root in changedEntries.Where(x => x.EntryState == EntryState.Modified).Select(x => x.NewEntry))
        {
    #pragma warning disable S3267 // Apply changes the group; a Where predicate should not have side effects
            foreach (var group in await GetLanguageVersionsAsync(root.Id))
            {
                if (MultilanguagePageInheritance.Apply(root, group))
                {
                    await (condition == null
                        ? groupedPageContentService.SaveGroupSettingsAsync(group)
                        : groupedPageContentService.SaveGroupSettingsAsync(group, condition));
                }
            }
#pragma warning restore S3267
        }
    }

    public override async Task DeleteAsync(IList<string> ids, bool softDelete = false)
    {
        foreach (var id in ids)
        {
            foreach (var group in await GetLanguageVersionsAsync(id))
            {
                await UnlinkAsync(group.Id);
            }
        }

        await base.DeleteAsync(ids, softDelete);
    }

    public virtual Task<MultilanguagePage> CreateAsync(MultilanguagePage model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        return CreateCheckedAsync(model);
    }

    private async Task<MultilanguagePage> CreateCheckedAsync(MultilanguagePage model)
    {
        await PrepareCreateAsync(model);
        await SaveChangesAsync([model]);

        return model;
    }

    public virtual Task<SiteBuilderChangePreview> PreviewCreateAsync(MultilanguagePage model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        return PreviewCreateCheckedAsync(model);
    }

    private async Task<SiteBuilderChangePreview> PreviewCreateCheckedAsync(MultilanguagePage model)
    {
        var root = model.CloneTyped();
        await PrepareCreateAsync(root);
        await PrepareSaveAsync(root);

        var preview = CreatePreview();
        preview.AddMultilanguagePage(from: null, root);

        return preview;
    }

    protected virtual async Task PrepareCreateAsync(MultilanguagePage model)
    {
        model.Id = null;
        if (!string.IsNullOrWhiteSpace(model.DefaultCultureName))
        {
            model.DefaultCultureName = await GetStoreLanguageAsync(model.StoreId, model.DefaultCultureName);
        }
    }

    public virtual Task<MultilanguagePage> UpdateAsync(MultilanguagePage model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        return UpdateCheckedAsync(model);
    }

    public virtual Task<MultilanguagePage> UpdateAsync(MultilanguagePage model, bool requireUnpublished, string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        return UpdateUnderConditionAsync(model, requireUnpublished, expectedVersion, cancellationToken);
    }

    private async Task<MultilanguagePage> UpdateCheckedAsync(MultilanguagePage model)
    {
        await PrepareUpdateAsync(model);
        await SaveChangesAsync([model]);

        return model;
    }

    private async Task<MultilanguagePage> UpdateUnderConditionAsync(MultilanguagePage model, bool requireUnpublished, string expectedVersion,
        CancellationToken cancellationToken)
    {
        // a published version changes only by the settings passed on to it, as the dry run tells it
        if (requireUnpublished && (await PreviewUpdateAsync(model, cancellationToken)).AffectsPublished)
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.PublishedPageMessage);
        }

        await PrepareUpdateAsync(model);
        if (expectedVersion != null)
        {
            await CheckRootVersionAsync(model.Id, expectedVersion.Trim(), cancellationToken);
        }

        LanguageVersionSaveCondition.Value = requireUnpublished ? new GroupedPageSaveCondition { RequireUnpublished = true } : null;
        try
        {
            await SaveChangesAsync([model]);
        }
        finally
        {
            LanguageVersionSaveCondition.Value = null;
        }

        return model;
    }

    /// <summary>The root has no lock of its own: it is read right before the save, as close to it as the storage allows.</summary>
    /// <exception cref="SiteBuilderContentConflictException">It is at another version, or gone.</exception>
    protected virtual async Task CheckRootVersionAsync(string multilanguagePageId, string expectedVersion, CancellationToken cancellationToken)
    {
        using var repository = repositoryFactory();
        await CheckRootVersionAsync(repository, multilanguagePageId, expectedVersion);
    }

    private static async Task CheckRootVersionAsync(ISiteBuilderRepository repository, string multilanguagePageId, string expectedVersion)
    {
        var stored = (await repository.GetMultilanguagePagesByIdsAsync([multilanguagePageId], null)).FirstOrDefault()
            ?.ToModel(AbstractTypeFactory<MultilanguagePage>.TryCreateInstance());

        if (stored == null || !string.Equals(SiteBuilderContentVersion.CreateForMultilanguagePage(stored), expectedVersion, StringComparison.Ordinal))
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.SettingsChangedMessage);
        }
    }

    public virtual Task<SiteBuilderChangePreview> PreviewUpdateAsync(MultilanguagePage model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        return PreviewUpdateCheckedAsync(model);
    }

    private async Task<SiteBuilderChangePreview> PreviewUpdateCheckedAsync(MultilanguagePage model)
    {
        var root = model.CloneTyped();
        var existing = await PrepareUpdateAsync(root);
        await PrepareSaveAsync(root);

        var preview = CreatePreview();
        // the version a caller sends back to update only the root it reviewed
        preview.AddMultilanguagePage(existing, root).ETag = SiteBuilderContentVersion.CreateForMultilanguagePage(existing);
        // the save passes the shared name and the inherited settings on, as AfterSaveChangesAsync does
        foreach (var group in await GetLanguageVersionsAsync(existing.Id))
        {
            var updated = group.CloneTyped();
            if (MultilanguagePageInheritance.Apply(root, updated))
            {
                preview.AddPage(group, updated);
            }
        }

        return preview;
    }

    /// <returns>The root as it is stored.</returns>
    protected virtual async Task<MultilanguagePage> PrepareUpdateAsync(MultilanguagePage model)
    {
        var existing = await GetRequiredAsync(model.Id);
        // A multilanguage page never moves to another store.
        model.StoreId = existing.StoreId;

        if (!string.IsNullOrWhiteSpace(model.DefaultCultureName) &&
            !string.Equals(model.DefaultCultureName, existing.DefaultCultureName, StringComparison.OrdinalIgnoreCase))
        {
            // The source language must be served by the store and have a version, like the page a convert starts from.
            // An unchanged one is not checked: a language removed from the store must not block editing the root.
            await GetStoreLanguageAsync(existing.StoreId, model.DefaultCultureName);

            var source = (await GetLanguageVersionsAsync(existing.Id))
                .FirstOrDefault(x => string.Equals(x.CultureName, model.DefaultCultureName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Multilanguage page '{existing.Id}' has no '{model.DefaultCultureName}' version.");
            model.DefaultCultureName = source.CultureName;
        }

        return existing;
    }

    public virtual Task<GroupedSiteBuilderPage> LinkAsync(string multilanguagePageId, string groupId, CancellationToken cancellationToken = default) =>
        LinkInternalAsync(multilanguagePageId, groupId, condition: null, cancellationToken);

    public virtual Task<GroupedSiteBuilderPage> LinkAsync(string multilanguagePageId, string groupId, bool requireUnpublished, string expectedVersion,
        string expectedPageVersion, CancellationToken cancellationToken = default) =>
        LinkInternalAsync(multilanguagePageId, groupId, new LinkCondition(requireUnpublished, expectedPageVersion?.Trim(), expectedVersion?.Trim()),
            cancellationToken);

    private async Task<GroupedSiteBuilderPage> LinkInternalAsync(string multilanguagePageId, string groupId, LinkCondition condition,
        CancellationToken cancellationToken)
    {
        var (root, group, cultureName) = await PrepareLinkAsync(multilanguagePageId, groupId);
        if (cultureName == null)
        {
            return group;
        }

        await SetMultilanguagePageIdAsync(groupId, root.Id, condition, cancellationToken);

        try
        {
            group = await groupedPageService.GetByIdAsync(groupId);
            // The language is stored as the store spells it, like a version created for it.
            group.CultureName = cultureName;
            // The save takes the root name and marks whatever differs from the root as overridden, so the page keeps
            // serving the same permalink and settings. It also reindexes the page with its translation group.
            await (condition?.ExpectedRootVersion == null
                ? groupedPageContentService.SaveGroupSettingsAsync(group, cancellationToken)
                : groupedPageContentService.SaveGroupSettingsAsync(group,
                    new GroupedPageSaveCondition { ExpectedMultilanguagePageVersion = condition.ExpectedRootVersion }, cancellationToken));
        }
        catch
        {
            // A failed or cancelled save (e.g. a concurrent version of the same language) must not leave the page linked.
            await TryRollbackAsync(() => SetMultilanguagePageIdAsync(groupId, null, condition: null, CancellationToken.None), $"unlink page '{groupId}'");
            throw;
        }

        return group;
    }

    public virtual async Task<SiteBuilderChangePreview> PreviewLinkAsync(string multilanguagePageId, string groupId, CancellationToken cancellationToken = default)
    {
        var (root, group, cultureName) = await PrepareLinkAsync(multilanguagePageId, groupId);
        var preview = CreatePreview();
        if (cultureName != null)
        {
            // the versions a caller sends back to link only the page it reviewed, with the values of the root it reviewed
            preview.AddPage(group, GetLinked(root, group, cultureName)).ETag = SiteBuilderContentVersion.CreateForGroupSettings(group);
            preview.AddObject(ModuleConstants.ChangePreview.MultilanguagePage, root.Id, SiteBuilderContentVersion.CreateForMultilanguagePage(root));
        }

        return preview;
    }

    /// <returns>The language the page is stored with once linked; <c>null</c> when it already is a version of the root.</returns>
    private async Task<(MultilanguagePage Root, GroupedSiteBuilderPage Group, string CultureName)> PrepareLinkAsync(string multilanguagePageId,
        string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var root = await GetRequiredAsync(multilanguagePageId);
        var group = await groupedPageService.GetByIdAsync(groupId)
            ?? throw new KeyNotFoundException($"Page '{groupId}' not found.");

        if (group.MultilanguagePageId == root.Id)
        {
            return (root, group, null);
        }

        if (!string.IsNullOrEmpty(group.MultilanguagePageId))
        {
            throw new InvalidOperationException($"Page '{groupId}' is already a language version of '{group.MultilanguagePageId}'.");
        }

        if (!string.Equals(group.StoreId, root.StoreId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Page '{groupId}' belongs to another store.");
        }

        var languages = await GetLanguageVersionsAsync(root.Id);
        if (languages.Any(x => string.Equals(x.CultureName, group.CultureName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Multilanguage page '{root.Id}' already has a '{group.CultureName}' version.");
        }

        // Checked before the link is written: the save below would reject the page, leaving it linked.
        if (string.IsNullOrWhiteSpace(group.CultureName))
        {
            throw new InvalidOperationException($"Page '{groupId}' must have a language to become a language version.");
        }

        // A version in a language the store does not serve has no storefront address, like a version created for it.
        return (root, group, await GetStoreLanguageAsync(root.StoreId, group.CultureName));
    }

    /// <summary>The page as the link and the save after it leave it.</summary>
    private static GroupedSiteBuilderPage GetLinked(MultilanguagePage root, GroupedSiteBuilderPage group, string cultureName)
    {
        var linked = group.CloneTyped();
        linked.MultilanguagePageId = root.Id;
        linked.SourceRevisionDate = null;
        linked.CultureName = cultureName;
        linked.Name = root.Name;
        MultilanguagePageInheritance.SetOverriddenFields(linked, MultilanguagePageInheritance.GetDifferingFields(root, linked));

        return linked;
    }

    public virtual Task<IList<string>> ResetOverridesAsync(string multilanguagePageId, IList<string> fields, CancellationToken cancellationToken = default) =>
        ResetOverridesInternalAsync(multilanguagePageId, fields, requireUnpublished: false, expectedVersion: null, expectedPageVersions: null, cancellationToken);

    public virtual async Task<IList<string>> ResetOverridesAsync(string multilanguagePageId, IList<string> fields, bool requireUnpublished,
        string expectedVersion, IDictionary<string, string> expectedPageVersions, CancellationToken cancellationToken = default)
    {
        // refused before the first save as the dry run tells it, then by each save under its lock
        if (requireUnpublished && (await PreviewResetOverridesAsync(multilanguagePageId, fields, cancellationToken)).AffectsPublished)
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.PublishedPageMessage);
        }

        return await ResetOverridesInternalAsync(multilanguagePageId, fields, requireUnpublished, expectedVersion?.Trim(), expectedPageVersions, cancellationToken);
    }

    private async Task<IList<string>> ResetOverridesInternalAsync(string multilanguagePageId, IList<string> fields, bool requireUnpublished,
        string expectedVersion, IDictionary<string, string> expectedPageVersions, CancellationToken cancellationToken)
    {
        var root = await GetRequiredAsync(multilanguagePageId);
        if (expectedVersion != null)
        {
            // the values the versions take: those of the root reviewed, as read and as stored right before the first save
            if (!string.Equals(SiteBuilderContentVersion.CreateForMultilanguagePage(root), expectedVersion, StringComparison.Ordinal))
            {
                throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.SettingsChangedMessage);
            }

            await CheckRootVersionAsync(root.Id, expectedVersion, cancellationToken);
        }

        var resets = new List<(GroupedSiteBuilderPage Group, string Version)>();
#pragma warning disable S3267 // Reset changes the group; a Where predicate should not have side effects
        foreach (var group in await GetLanguageVersionsAsync(root.Id))
        {
            var version = SiteBuilderContentVersion.CreateForGroupSettings(group);
            // The save derives the mask again, so the reset fields become inherited.
            if (MultilanguagePageInheritance.Reset(root, group, fields ?? []))
            {
                resets.Add((group, version));
            }
        }
#pragma warning restore S3267

        if (expectedPageVersions != null)
        {
            CheckResetVersions(resets, expectedPageVersions);
        }

        var changedCultureNames = new List<string>();
        foreach (var (group, version) in resets)
        {
            // compared again under the lock of the save, and the root after the save derived the overrides from it
            var expectedPageVersion = expectedPageVersions == null ? null : version;
            await (requireUnpublished || expectedPageVersion != null || expectedVersion != null
                ? groupedPageContentService.SaveGroupSettingsAsync(group, new GroupedPageSaveCondition
                {
                    RequireUnpublished = requireUnpublished,
                    ExpectedSettingsVersion = expectedPageVersion,
                    ExpectedMultilanguagePageVersion = expectedVersion,
                }, cancellationToken)
                : groupedPageContentService.SaveGroupSettingsAsync(group, cancellationToken));
            changedCultureNames.Add(group.CultureName);
        }

        return changedCultureNames;
    }

    /// <summary>The versions the reset changes must be those reviewed: none changed, none added and none gone.</summary>
    /// <exception cref="SiteBuilderContentConflictException">They are not.</exception>
    private static void CheckResetVersions(IList<(GroupedSiteBuilderPage Group, string Version)> resets, IDictionary<string, string> expectedPageVersions)
    {
        if (resets.Count != expectedPageVersions.Count ||
            resets.Any(x => !expectedPageVersions.TryGetValue(x.Group.Id, out var version) || !string.Equals(version?.Trim(), x.Version, StringComparison.Ordinal)))
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.SettingsChangedMessage);
        }
    }

    public virtual async Task<SiteBuilderChangePreview> PreviewResetOverridesAsync(string multilanguagePageId, IList<string> fields,
        CancellationToken cancellationToken = default)
    {
        var root = await GetRequiredAsync(multilanguagePageId);
        var preview = CreatePreview();

        foreach (var group in await GetLanguageVersionsAsync(root.Id))
        {
            var reset = group.CloneTyped();
            if (MultilanguagePageInheritance.Reset(root, reset, fields ?? []))
            {
                // the save derives the mask again
                MultilanguagePageInheritance.SetOverriddenFields(reset, MultilanguagePageInheritance.GetDifferingFields(root, reset));
                // the version a caller sends back to reset only the versions it reviewed
                if (preview.AddPage(group, reset) is { } change)
                {
                    change.ETag = SiteBuilderContentVersion.CreateForGroupSettings(group);
                }
            }
        }

        if (preview.Objects.Count > 0)
        {
            // the values the versions take: the version a caller sends back to reset them to the root it reviewed
            preview.AddObject(ModuleConstants.ChangePreview.MultilanguagePage, root.Id, SiteBuilderContentVersion.CreateForMultilanguagePage(root));
        }

        return preview;
    }

    public virtual async Task<GroupedSiteBuilderPage> MarkUpToDateAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var (group, source) = await PrepareMarkUpToDateAsync(groupId);

        group.SourceRevisionDate = source.SourceRevisionDate;
        await groupedPageService.SaveChangesAsync([group]);

        return group;
    }

    public virtual async Task<GroupedSiteBuilderPage> MarkUpToDateAsync(string groupId, DateTime? expectedSourceRevisionDate,
        CancellationToken cancellationToken = default)
    {
        if (expectedSourceRevisionDate == null)
        {
            return await MarkUpToDateAsync(groupId, cancellationToken);
        }

        var (group, source) = await PrepareMarkUpToDateAsync(groupId, expectedSourceRevisionDate);

        // the stored revision, read again under the lock of the save, which the source's next publication takes too
        group.SourceRevisionDate = source.SourceRevisionDate;
        var condition = new GroupedPageSaveCondition { SourceGroupId = source.Id, ExpectedSourceRevisionDate = source.SourceRevisionDate };
        await groupedPageContentService.SaveGroupAsync(group, condition, cancellationToken);

        return group;
    }

    public virtual Task<SiteBuilderChangePreview> PreviewMarkUpToDateAsync(string groupId, CancellationToken cancellationToken = default) =>
        PreviewMarkUpToDateAsync(groupId, expectedSourceRevisionDate: null, cancellationToken);

    public virtual async Task<SiteBuilderChangePreview> PreviewMarkUpToDateAsync(string groupId, DateTime? expectedSourceRevisionDate,
        CancellationToken cancellationToken = default)
    {
        var (group, source) = await PrepareMarkUpToDateAsync(groupId, expectedSourceRevisionDate);

        var marked = group.CloneTyped();
        marked.SourceRevisionDate = source.SourceRevisionDate;
        var preview = CreatePreview();
        preview.AddPage(group, marked);

        return preview;
    }

    /// <returns>The translation, and the source language version whose publication it is to be based on.</returns>
    /// <exception cref="SiteBuilderContentConflictException">The source revision is not <paramref name="expectedSourceRevisionDate"/>.</exception>
    private async Task<(GroupedSiteBuilderPage Group, GroupedSiteBuilderPage Source)> PrepareMarkUpToDateAsync(string groupId,
        DateTime? expectedSourceRevisionDate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var group = await groupedPageService.GetByIdAsync(groupId)
            ?? throw new KeyNotFoundException($"Page '{groupId}' not found.");
        var root = await GetRequiredAsync(group.MultilanguagePageId);

        var source = (await GetLanguageVersionsAsync(root.Id))
            .FirstOrDefault(x => string.Equals(x.CultureName, root.DefaultCultureName, StringComparison.OrdinalIgnoreCase));

        if (source?.Id == group.Id)
        {
            throw new InvalidOperationException($"Page '{groupId}' is the source language version: it is never outdated.");
        }

        // Without a source publication there is nothing to be up to date with, and the call would change nothing.
        if (source?.SourceRevisionDate == null)
        {
            throw new InvalidOperationException($"The source language '{root.DefaultCultureName}' version has not been published yet.");
        }

        if (expectedSourceRevisionDate != null && !IsSameRevision(source.SourceRevisionDate.Value, expectedSourceRevisionDate.Value))
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.SourceRevisionChangedMessage);
        }

        return (group, source);
    }

    // A revision a client sends back may have lost a fraction of a millisecond on its way through JSON; two publications are further apart.
    private static bool IsSameRevision(DateTime stored, DateTime expected) =>
        Math.Abs((ToUtc(stored) - ToUtc(expected)).TotalMilliseconds) < 1;

    // Revisions are stored in UTC, whatever kind the database or the client gives them.
    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public virtual async Task<MultilanguagePage> CloneAsync(string multilanguagePageId, CancellationToken cancellationToken = default)
    {
        var (clone, versions, number) = await PrepareCloneAsync(multilanguagePageId, cancellationToken);
        await SaveChangesAsync([clone]);

        foreach (var version in versions)
        {
            var content = (await groupedPageContentService.LoadGroupContentAsync(version, draft: true, cancellationToken)).Content;

            var group = CreateVersionCopy(clone, version, number);
            var draft = AbstractTypeFactory<SiteBuilderPage>.TryCreateInstance();
            draft.Status = ModuleConstants.PageStatuses.Draft;
            draft.StoreId = clone.StoreId;
            draft.Content = SiteBuilderContentSettings.Synchronize(content, group);
            group.Pages.Add(draft);

            await groupedPageService.SaveChangesAsync([group]);
        }

        return clone;
    }

    public virtual async Task<SiteBuilderChangePreview> PreviewCloneAsync(string multilanguagePageId, CancellationToken cancellationToken = default)
    {
        var (clone, versions, number) = await PrepareCloneAsync(multilanguagePageId, cancellationToken);

        var preview = CreatePreview();
        preview.AddMultilanguagePage(from: null, clone);
        foreach (var version in versions)
        {
            preview.AddPage(from: null, CreateVersionCopy(clone, version, number));
        }

        return preview;
    }

    /// <returns>The copy of the root, not saved yet; the versions to copy; the copy suffix number.</returns>
    private async Task<(MultilanguagePage Clone, IList<GroupedSiteBuilderPage> Versions, int Number)> PrepareCloneAsync(string multilanguagePageId,
        CancellationToken cancellationToken)
    {
        var source = await GetRequiredAsync(multilanguagePageId);
        var versions = await GetLanguageVersionsAsync(source.Id);

        int number;
        using (var repository = repositoryFactory())
        {
            number = await repository.FindFreeCopyNumberAsync(source.StoreId,
                [source.Permalink, .. versions.Select(x => x.Permalink)], cancellationToken);
        }

        var clone = source.CloneTyped();
        clone.Id = null;
        clone.Name = SiteBuilderPageCopy.GetName(source.Name, number);
        clone.Permalink = SiteBuilderPageCopy.GetPermalink(source.Permalink, number);

        return (clone, versions, number);
    }

    /// <summary>The copy of a language version for the copy of its root, without its draft.</summary>
    protected virtual GroupedSiteBuilderPage CreateVersionCopy(MultilanguagePage clone, GroupedSiteBuilderPage version, int number)
    {
        var group = AbstractTypeFactory<GroupedSiteBuilderPage>.TryCreateInstance();
        group.StoreId = clone.StoreId;
        group.CultureName = version.CultureName;
        group.MultilanguagePageId = clone.Id;
        group.Permalink = SiteBuilderPageCopy.GetPermalink(version.Permalink, number);
        group.Visibility = version.Visibility;
        group.UserGroups = version.UserGroups;
        group.OrganizationId = version.OrganizationId;
        group.StartDate = version.StartDate;
        group.EndDate = version.EndDate;
        group.SourceRevisionDate = version.SourceRevisionDate;
        // Overridden fields keep the version's values, the others follow the copy's root.
        group.OverriddenFields = version.OverriddenFields;
        MultilanguagePageInheritance.Apply(clone, group);

        return group;
    }

    public virtual Task<MultilanguagePage> ConvertAsync(string groupId, CancellationToken cancellationToken = default) =>
        ConvertInternalAsync(groupId, requireUnpublished: false, expectedVersion: null, cancellationToken);

    public virtual Task<MultilanguagePage> ConvertAsync(string groupId, bool requireUnpublished, string expectedVersion, CancellationToken cancellationToken = default) =>
        ConvertInternalAsync(groupId, requireUnpublished, expectedVersion?.Trim(), cancellationToken);

    private async Task<MultilanguagePage> ConvertInternalAsync(string groupId, bool requireUnpublished, string expectedVersion, CancellationToken cancellationToken)
    {
        var (group, root) = await PrepareConvertAsync(groupId);
        // before the root is created, then again under the lock of the link
        if (requireUnpublished && group.Status == ModuleConstants.PageStatuses.Published)
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.PublishedPageMessage);
        }

        if (expectedVersion != null && !string.Equals(SiteBuilderContentVersion.CreateForGroupSettings(group), expectedVersion, StringComparison.Ordinal))
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.SettingsChangedMessage);
        }

        await SaveChangesAsync([root]);

        try
        {
            await (requireUnpublished || expectedVersion != null
                ? LinkAsync(root.Id, groupId, requireUnpublished, expectedVersion: null, expectedPageVersion: expectedVersion, cancellationToken)
                : LinkAsync(root.Id, groupId, cancellationToken));
        }
        catch
        {
            // The root exists only for this page; without it the root would stay as an empty Multilanguage Page.
            await TryRollbackAsync(() => base.DeleteAsync([root.Id]), $"delete multilanguage page '{root.Id}'");
            throw;
        }

        return root;
    }

    public virtual async Task<SiteBuilderChangePreview> PreviewConvertAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var (group, root) = await PrepareConvertAsync(groupId);

        var preview = CreatePreview();
        preview.AddMultilanguagePage(from: null, root);
        // the version a caller sends back to convert only the page it reviewed
        var version = SiteBuilderContentVersion.CreateForGroupSettings(group);
        if (preview.AddPage(group, GetLinked(root, group, root.DefaultCultureName)) is { } change)
        {
            change.ETag = version;
        }
        else
        {
            // the link is to a root without an id yet, so the page is listed even when none of its fields change
            preview.AddObject(ModuleConstants.ChangePreview.Page, group.Id, version);
            preview.AffectsPublished |= group.Status == ModuleConstants.PageStatuses.Published;
        }

        return preview;
    }

    /// <returns>The page, and the root it becomes the default language version of, not saved yet.</returns>
    private async Task<(GroupedSiteBuilderPage Group, MultilanguagePage Root)> PrepareConvertAsync(string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var group = await groupedPageService.GetByIdAsync(groupId)
            ?? throw new KeyNotFoundException($"Page '{groupId}' not found.");

        if (!string.IsNullOrEmpty(group.MultilanguagePageId))
        {
            throw new InvalidOperationException($"Page '{groupId}' is already a language version of '{group.MultilanguagePageId}'.");
        }

        // The page becomes the source language, so its language must be one the store serves.
        var store = await storeService.GetByIdAsync(group.StoreId);
        if (string.IsNullOrWhiteSpace(group.CultureName) ||
            store?.Languages?.Contains(group.CultureName, StringComparer.OrdinalIgnoreCase) != true)
        {
            throw new InvalidOperationException($"Page '{groupId}' must have a language of store '{group.StoreId}'.");
        }

        var root = AbstractTypeFactory<MultilanguagePage>.TryCreateInstance();
        root.StoreId = group.StoreId;
        root.Name = group.Name;
        root.Permalink = group.Permalink;
        root.DefaultCultureName = store.Languages.First(x => string.Equals(x, group.CultureName, StringComparison.OrdinalIgnoreCase));
        root.OrganizationId = group.OrganizationId;
        root.Visibility = group.Visibility;
        root.UserGroups = group.UserGroups;
        root.StartDate = group.StartDate;
        root.EndDate = group.EndDate;

        return (group, root);
    }

    public virtual Task<GroupedSiteBuilderPage> UnlinkAsync(string groupId, CancellationToken cancellationToken = default) =>
        UnlinkInternalAsync(groupId, condition: null, cancellationToken);

    public virtual Task<GroupedSiteBuilderPage> UnlinkAsync(string groupId, bool requireUnpublished, string expectedVersion,
        CancellationToken cancellationToken = default) =>
        UnlinkInternalAsync(groupId, new LinkCondition(requireUnpublished, expectedVersion?.Trim(), ExpectedRootVersion: null), cancellationToken);

    private async Task<GroupedSiteBuilderPage> UnlinkInternalAsync(string groupId, LinkCondition condition, CancellationToken cancellationToken)
    {
        var group = await GetRequiredGroupAsync(groupId);

        if (string.IsNullOrEmpty(group.MultilanguagePageId))
        {
            return group;
        }

        await SetMultilanguagePageIdAsync(groupId, null, condition, cancellationToken);

        group = await groupedPageService.GetByIdAsync(groupId);
        // The page already stores effective values, so it keeps serving exactly what it served as a language version.
        MultilanguagePageInheritance.SetOverriddenFields(group, []);
        await groupedPageContentService.SaveGroupSettingsAsync(group, cancellationToken);

        return group;
    }

    public virtual async Task<SiteBuilderChangePreview> PreviewUnlinkAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var group = await GetRequiredGroupAsync(groupId);
        var preview = CreatePreview();

        if (!string.IsNullOrEmpty(group.MultilanguagePageId))
        {
            var unlinked = group.CloneTyped();
            unlinked.MultilanguagePageId = null;
            unlinked.SourceRevisionDate = null;
            MultilanguagePageInheritance.SetOverriddenFields(unlinked, []);
            // the version a caller sends back to unlink only the page it reviewed
            preview.AddPage(group, unlinked).ETag = SiteBuilderContentVersion.CreateForGroupSettings(group);
        }

        return preview;
    }

    private async Task<GroupedSiteBuilderPage> GetRequiredGroupAsync(string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        return await groupedPageService.GetByIdAsync(groupId)
            ?? throw new KeyNotFoundException($"Page '{groupId}' not found.");
    }

    public virtual async Task<IList<MultilanguagePageLanguage>> GetLanguagesAsync(string multilanguagePageId, CancellationToken cancellationToken = default)
    {
        var root = await GetRequiredAsync(multilanguagePageId);
        var store = await storeService.GetByIdAsync(root.StoreId);

        return MultilanguagePageLanguages.Build(root, store?.Languages, await GetLanguageVersionsAsync(root.Id));
    }

    public virtual async Task<GroupedSiteBuilderPage> CreateLanguageAsync(string multilanguagePageId, string cultureName, bool copyContent, CancellationToken cancellationToken = default)
    {
        var (root, group) = await PrepareLanguageAsync(multilanguagePageId, cultureName, cancellationToken);

        string sourceContent = null;
        if (copyContent)
        {
            var source = await GetSourceVersionAsync(root);
            if (source != null)
            {
                sourceContent = (await groupedPageContentService.LoadGroupContentAsync(source, draft: true, cancellationToken)).Content;
                // A copy of the source is up to date with it.
                group.SourceRevisionDate = source.SourceRevisionDate;
            }
        }

        var draft = AbstractTypeFactory<SiteBuilderPage>.TryCreateInstance();
        draft.Status = ModuleConstants.PageStatuses.Draft;
        draft.StoreId = root.StoreId;
        // The copied document still carries the source language settings (permalink, culture).
        draft.Content = SiteBuilderContentSettings.Synchronize(sourceContent, group);
        group.Pages.Add(draft);

        await groupedPageService.SaveChangesAsync([group]);

        return group;
    }

    public virtual async Task<SiteBuilderChangePreview> PreviewCreateLanguageAsync(string multilanguagePageId, string cultureName, bool copyContent,
        CancellationToken cancellationToken = default)
    {
        var (root, group) = await PrepareLanguageAsync(multilanguagePageId, cultureName, cancellationToken);
        if (copyContent && await GetSourceVersionAsync(root) is { } source)
        {
            group.SourceRevisionDate = source.SourceRevisionDate;
        }

        var preview = CreatePreview();
        preview.AddPage(from: null, group);

        return preview;
    }

    /// <returns>The root, and its new version, without its draft and not saved yet.</returns>
    private async Task<(MultilanguagePage Root, GroupedSiteBuilderPage Group)> PrepareLanguageAsync(string multilanguagePageId, string cultureName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureName);

        var root = await GetRequiredAsync(multilanguagePageId);
        var language = (await GetLanguagesAsync(root.Id, cancellationToken))
            .FirstOrDefault(x => string.Equals(x.CultureName, cultureName, StringComparison.OrdinalIgnoreCase));

        if (language is not { IsStoreLanguage: true })
        {
            throw new InvalidOperationException($"'{cultureName}' is not a language of store '{root.StoreId}'.");
        }

        if (language.Page != null)
        {
            throw new InvalidOperationException($"Multilanguage page '{root.Id}' already has a '{language.CultureName}' version.");
        }

        var group = AbstractTypeFactory<GroupedSiteBuilderPage>.TryCreateInstance();
        group.StoreId = root.StoreId;
        group.CultureName = language.CultureName;
        group.MultilanguagePageId = root.Id;
        MultilanguagePageInheritance.Apply(root, group);

        return (root, group);
    }

    private async Task<GroupedSiteBuilderPage> GetSourceVersionAsync(MultilanguagePage root) =>
        (await GetLanguageVersionsAsync(root.Id))
            .FirstOrDefault(x => string.Equals(x.CultureName, root.DefaultCultureName, StringComparison.OrdinalIgnoreCase));

    public virtual Task<MultilanguagePageBatchResult> PublishAllAsync(string multilanguagePageId, CancellationToken cancellationToken = default) =>
        PublishAllInternalAsync(multilanguagePageId, expectedDraftVersions: null, expectedSettingsVersions: null, cancellationToken);

    public virtual Task<MultilanguagePageBatchResult> PublishAllAsync(string multilanguagePageId, IDictionary<string, string> expectedDraftVersions,
        IDictionary<string, string> expectedSettingsVersions, CancellationToken cancellationToken = default) =>
        PublishAllInternalAsync(multilanguagePageId, expectedDraftVersions, expectedSettingsVersions, cancellationToken);

    private async Task<MultilanguagePageBatchResult> PublishAllInternalAsync(string multilanguagePageId, IDictionary<string, string> expectedDraftVersions,
        IDictionary<string, string> expectedSettingsVersions, CancellationToken cancellationToken)
    {
        var root = await GetRequiredAsync(multilanguagePageId);

        return await distributedLock.ExecuteAsync(GetBatchLockResource(root.Id),
            _ => PublishAllUnderLockAsync(root, expectedDraftVersions, expectedSettingsVersions, cancellationToken),
            cancellationToken: cancellationToken);
    }

    private async Task<MultilanguagePageBatchResult> PublishAllUnderLockAsync(MultilanguagePage root, IDictionary<string, string> expectedDraftVersions,
        IDictionary<string, string> expectedSettingsVersions, CancellationToken cancellationToken)
    {
        var result = new MultilanguagePageBatchResult();

        // The source language goes first: translations published in the same batch are based on its new revision.
        var groups = (await GetLanguageVersionsAsync(root.Id))
            .OrderByDescending(x => string.Equals(x.CultureName, root.DefaultCultureName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (expectedDraftVersions != null || expectedSettingsVersions != null)
        {
            await CheckDraftVersionsAsync(groups, expectedDraftVersions, expectedSettingsVersions, cancellationToken);
        }

        foreach (var group in groups)
        {
            var skipReason = await publishConflictService.ExecuteExclusiveAsync(group,
                () => PublishVersionAsync(root, group.Id, expectedDraftVersions, expectedSettingsVersions, cancellationToken), cancellationToken);
            if (skipReason == null)
            {
                result.ProcessedCultureNames.Add(group.CultureName);
            }
            else
            {
                result.Skipped.Add(new MultilanguagePageBatchSkip { CultureName = group.CultureName, Reason = skipReason });
            }
        }

        return result;
    }

    /// <summary>
    /// The drafts, and the settings they go live with, must be those reviewed: none changed, none added and none gone, as each one goes live.
    /// A <c>null</c> set is not compared.
    /// </summary>
    /// <exception cref="SiteBuilderContentConflictException">They are not.</exception>
    protected virtual async Task CheckDraftVersionsAsync(IList<GroupedSiteBuilderPage> groups, IDictionary<string, string> expectedDraftVersions,
        IDictionary<string, string> expectedSettingsVersions, CancellationToken cancellationToken)
    {
        var drafted = groups.Where(x => GroupedPagePublishing.GetDraft(x) != null).ToList();
        if ((expectedDraftVersions != null && drafted.Count != expectedDraftVersions.Count) ||
            (expectedSettingsVersions != null && drafted.Count != expectedSettingsVersions.Count))
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.PageChangedMessage);
        }

        foreach (var group in drafted)
        {
            if (expectedSettingsVersions != null && !IsAtSettingsVersion(group, expectedSettingsVersions))
            {
                throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.PageChangedMessage);
            }

            if (expectedDraftVersions == null)
            {
                continue;
            }

            var current = await groupedPageContentService.LoadGroupContentAsync(group, draft: true, cancellationToken);
            if (!expectedDraftVersions.TryGetValue(group.Id, out var eTag) || !string.Equals(eTag?.Trim(), current.ETag, StringComparison.Ordinal))
            {
                throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.PageChangedMessage);
            }
        }
    }

    private static bool IsAtSettingsVersion(GroupedSiteBuilderPage group, IDictionary<string, string> expectedSettingsVersions) =>
        expectedSettingsVersions.TryGetValue(group.Id, out var version) &&
        string.Equals(version?.Trim(), SiteBuilderContentVersion.CreateForGroupSettings(group), StringComparison.Ordinal);

    /// <summary>
    /// Runs under the lock on the permalink of the version, so the version is reloaded: a single-page publish may have changed it meanwhile.
    /// A version that would replace another published page is skipped: unpublishing that page is a choice the author makes when publishing
    /// the version alone.
    /// </summary>
    /// <returns>Why the version is skipped; <c>null</c> when it is published.</returns>
    private async Task<string> PublishVersionAsync(MultilanguagePage root, string groupId, IDictionary<string, string> expectedDraftVersions,
        IDictionary<string, string> expectedSettingsVersions, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.GetByIdAsync(groupId);
        if (group == null || GroupedPagePublishing.GetDraft(group) == null)
        {
            return MultilanguagePageBatchResult.NoDraftReason;
        }

        // the address and audience it would go live with changed after the check, or its draft appeared after it
        if (expectedSettingsVersions != null && !IsAtSettingsVersion(group, expectedSettingsVersions))
        {
            return MultilanguagePageBatchResult.ChangedReason;
        }

        var (conflicts, _) = await publishConflictService.FindBlockingConflictsAsync(group);
        if (conflicts.Count > 0)
        {
            return MultilanguagePageBatchResult.PermalinkConflictReason;
        }

        GroupedPagePublishing.TryPublishDraft(group, out var obsoletePageIds);

        try
        {
            await SavePublishedVersionAsync(group, expectedDraftVersions, expectedSettingsVersions, cancellationToken);
        }
        catch (SiteBuilderContentConflictException)
        {
            // changed after the check above: what would go live is not what was reviewed
            ExpireCachedVersion(group);
            return MultilanguagePageBatchResult.ChangedReason;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One failing language does not stop the others; the summary reports it.
            LogPublishFailure(ex, root, group);
            ExpireCachedVersion(group);
            return MultilanguagePageBatchResult.FailedReason;
        }

        try
        {
            await pageService.DeleteAsync(obsoletePageIds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A version whose obsolete pages were not removed is still published; they are cleaned up on its next publish.
            LogPublishFailure(ex, root, group);
            ExpireCachedVersion(group);
        }

        return null;
    }

    /// <summary>With versions expected, the draft and the settings are compared again under the lock of its save.</summary>
    protected virtual Task SavePublishedVersionAsync(GroupedSiteBuilderPage group, IDictionary<string, string> expectedDraftVersions,
        IDictionary<string, string> expectedSettingsVersions, CancellationToken cancellationToken)
    {
        if (expectedDraftVersions == null || !expectedDraftVersions.TryGetValue(group.Id, out var eTag))
        {
            return groupedPageService.SaveChangesAsync([group]);
        }

        string settingsVersion = null;
        expectedSettingsVersions?.TryGetValue(group.Id, out settingsVersion);

        return groupedPageContentService.SaveGroupAtVersionAsync(group, draft: true, eTag?.Trim(), settingsVersion?.Trim(), cancellationToken);
    }

    private void LogPublishFailure(Exception ex, MultilanguagePage root, GroupedSiteBuilderPage group) =>
        logger.LogError(ex, "Publishing the {CultureName} version '{GroupId}' of multilanguage page '{MultilanguagePageId}' failed.",
            group.CultureName, group.Id, root.Id);

    // The publish changed a shared cached copy of the version, so the cache must not serve it.
    private static void ExpireCachedVersion(GroupedSiteBuilderPage group)
    {
        GenericCachingRegion<GroupedSiteBuilderPage>.ExpireTokenForKey(group.Id);
        GenericSearchCachingRegion<GroupedSiteBuilderPage>.ExpireRegion();
    }

    public virtual async Task<SiteBuilderChangePreview> PreviewPublishAllAsync(string multilanguagePageId, CancellationToken cancellationToken = default)
    {
        var root = await GetRequiredAsync(multilanguagePageId);
        var preview = CreatePreview();

        foreach (var group in (await GetLanguageVersionsAsync(root.Id)).Where(x => GroupedPagePublishing.GetDraft(x) != null))
        {
            // the version a caller sends back to publish only the drafts it reviewed
            var current = await groupedPageContentService.LoadGroupContentAsync(group, draft: true, cancellationToken);
            preview.AddPublishing(group, ModuleConstants.PageStatuses.Draft, ModuleConstants.PageStatuses.Published).ETag = current.ETag;
        }

        return preview;
    }

    public virtual async Task<MultilanguagePageBatchResult> ArchiveAllAsync(string multilanguagePageId, CancellationToken cancellationToken = default)
    {
        var root = await GetRequiredAsync(multilanguagePageId);

        return await distributedLock.ExecuteAsync(GetBatchLockResource(root.Id), _ => ArchiveAllUnderLockAsync(root),
            cancellationToken: cancellationToken);
    }

    private async Task<MultilanguagePageBatchResult> ArchiveAllUnderLockAsync(MultilanguagePage root)
    {
        var result = new MultilanguagePageBatchResult();
        var changed = new List<GroupedSiteBuilderPage>();

        foreach (var group in await GetLanguageVersionsAsync(root.Id))
        {
            if (GroupedPagePublishing.Archive(group))
            {
                changed.Add(group);
                result.ProcessedCultureNames.Add(group.CultureName);
            }
            else
            {
                result.Skipped.Add(new MultilanguagePageBatchSkip { CultureName = group.CultureName, Reason = MultilanguagePageBatchResult.AlreadyArchivedReason });
            }
        }

        if (changed.Count > 0)
        {
            await groupedPageService.SaveChangesAsync(changed);
        }

        return result;
    }

    // Batches of one page run one at a time and read the versions under the lock: two parallel publish-all calls would
    // otherwise both publish the same drafts, and the source revision of the second save would not mark translations outdated.
    private static string GetBatchLockResource(string multilanguagePageId)
    {
        return $"sitebuilder:multilanguage-page:batch:{multilanguagePageId}";
    }

    // A failing rollback is logged, so the caller still gets the error that caused it.
    private async Task TryRollbackAsync(Func<Task> rollback, string description)
    {
        try
        {
            await rollback();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rollback failed: could not {Rollback}.", description);
        }
    }

    protected virtual SiteBuilderChangePreview CreatePreview() => AbstractTypeFactory<SiteBuilderChangePreview>.TryCreateInstance();

    // The store spelling of the language ("en-US" for "EN-us"): versions, the index and the storefront compare it as stored.
    private async Task<string> GetStoreLanguageAsync(string storeId, string cultureName)
    {
        var store = await storeService.GetNoCloneAsync(storeId);

        return store?.Languages?.FirstOrDefault(x => string.Equals(x, cultureName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"'{cultureName}' is not a language of store '{storeId}'.");
    }

    private async Task<MultilanguagePage> GetRequiredAsync(string multilanguagePageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(multilanguagePageId);

        return await this.GetByIdAsync(multilanguagePageId)
            ?? throw new KeyNotFoundException($"Multilanguage page '{multilanguagePageId}' not found.");
    }

    protected virtual async Task<IList<GroupedSiteBuilderPage>> GetLanguageVersionsAsync(string multilanguagePageId)
    {
        var criteria = AbstractTypeFactory<SiteBuilderPageSearchCriteria>.TryCreateInstance();
        criteria.MultilanguagePageId = multilanguagePageId;

        return await groupedPageSearchService.SearchAllAsync(criteria);
    }

    /// <param name="condition">What must hold, as read under the lock of the write; <c>null</c> writes whatever the page holds.</param>
    /// <exception cref="SiteBuilderContentConflictException">It does not hold: nothing is written.</exception>
    private async Task SetMultilanguagePageIdAsync(string groupId, string multilanguagePageId, LinkCondition condition, CancellationToken cancellationToken)
    {
        // A regular grouped save never patches the link (see GroupedSiteBuilderPageEntity.Patch), so it is written here,
        // under the same lock as other grouped writers.
        using (var repository = repositoryFactory())
        {
            await repository.ExecuteUnderGroupedPageWriteLocksAsync(
                [groupId],
                async ct =>
                {
                    var entity = await repository.GroupedSiteBuilderPages.FirstOrDefaultAsync(x => x.Id == groupId, ct)
                        ?? throw new KeyNotFoundException($"Page '{groupId}' not found.");
                    if (condition != null)
                    {
                        await CheckLinkConditionAsync(repository, entity, multilanguagePageId, condition, ct);
                    }
                    entity.MultilanguagePageId = multilanguagePageId;
                    // The revision a translation was based on belongs to the previous root, if any.
                    entity.SourceRevisionDate = null;
                    await repository.UnitOfWork.CommitAsync();
                },
                cancellationToken);
        }

        GenericCachingRegion<GroupedSiteBuilderPage>.ExpireTokenForKey(groupId);
        GenericSearchCachingRegion<GroupedSiteBuilderPage>.ExpireRegion();
    }

    private static async Task CheckLinkConditionAsync(ISiteBuilderRepository repository, GroupedSiteBuilderPageEntity entity, string multilanguagePageId,
        LinkCondition condition, CancellationToken cancellationToken)
    {
        if (condition.ExpectedPageVersion != null && !string.Equals(
                SiteBuilderContentVersion.CreateForGroupSettings(entity.ToModel(AbstractTypeFactory<GroupedSiteBuilderPage>.TryCreateInstance())),
                condition.ExpectedPageVersion, StringComparison.Ordinal))
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.SettingsChangedMessage);
        }

        // a link or an unlink of a published page changes what it serves
        if (condition.RequireUnpublished &&
            await repository.SiteBuilderPages.AnyAsync(x => x.GroupId == entity.Id && x.Status == ModuleConstants.PageStatuses.Published, cancellationToken))
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.PublishedPageMessage);
        }

        if (condition.ExpectedRootVersion != null)
        {
            await CheckRootVersionAsync(repository, multilanguagePageId, condition.ExpectedRootVersion);
        }
    }

    /// <summary>What a link or an unlink requires of the page, and a link of its root; the versions are trimmed.</summary>
    private sealed record LinkCondition(bool RequireUnpublished, string ExpectedPageVersion, string ExpectedRootVersion);
}
