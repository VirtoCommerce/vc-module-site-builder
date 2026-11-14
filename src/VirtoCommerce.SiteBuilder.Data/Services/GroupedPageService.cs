using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.Pages.Core.Events;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Platform.Caching;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.GenericCrud;
using VirtoCommerce.Platform.Data.GenericCrud;
using VirtoCommerce.Platform.Data.Infrastructure;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Data.Services
{
    public class GroupedPageService
        : CrudService<GroupedSiteBuilderPage, GroupedSiteBuilderPageEntity, GroupedSiteBuilderPageChangingEvent,
                GroupedSiteBuilderPageChangedEvent>,
          IGroupedPageService, IGroupedPageContentService
    {
        private const int ExistingGroupsQueryBatchSize = 500;

        private readonly Func<IContentStreamRepository> _contentStreamRepositoryFactory;
        private readonly IEventPublisher _eventPublisher;
        private readonly ILogger<GroupedPageService> _logger;
        private readonly Func<ISiteBuilderRepository> _repositoryFactory;

        public GroupedPageService(
            Func<ISiteBuilderRepository> repositoryFactory,
            Func<IContentStreamRepository> contentStreamRepositoryFactory,
            IPlatformMemoryCache platformMemoryCache,
            IEventPublisher eventPublisher,
            ILogger<GroupedPageService> logger)
            : base(repositoryFactory, platformMemoryCache, eventPublisher)
        {
            _repositoryFactory = repositoryFactory;
            _contentStreamRepositoryFactory = contentStreamRepositoryFactory;
            _eventPublisher = eventPublisher;
            _logger = logger;
        }

        // The generic CRUD hook runs before its repository transaction, which leaves a gap where a page can
        // acquire a Shared Component reference after store validation. Repositories that support Shared
        // Components keep the validation and commit under the same write locks as raw content writers.
        public override Task SaveChangesAsync(IList<GroupedSiteBuilderPage> models)
        {
            return SaveChangesInternalAsync(models);
        }

        private async Task SaveChangesInternalAsync(
            IList<GroupedSiteBuilderPage> models,
            Func<ISiteBuilderRepository, IList<GroupedSiteBuilderPageEntity>, CancellationToken, Task<PreparedSave>> prepare = null,
            CancellationToken cancellationToken = default)
        {
            var groupedEventEntries = new List<GenericChangedEntry<GroupedSiteBuilderPage>>();
            var primaryKeyMap = new PrimaryKeyResolvingMap();
            var changedEntries = new GenericChangedEntry<GroupedSiteBuilderPage>[models.Count];
            var changedEntities = new GroupedSiteBuilderPageEntity[models.Count];
            var originalModels = new List<GroupedSiteBuilderPage>();

            using (var repository = _repositoryFactory())
            {
                var groupIds = models
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                    .Select(x => x.Id)
                    .ToArray();

                async Task SaveInternalAsync(CancellationToken ct)
                {
                    var publishGroupedEvents = true;
                    var existingEntities = await LoadExistingEntities(repository, models);
                    if (prepare != null)
                    {
                        var prepared = await prepare(repository, existingEntities, ct);
                        if (prepared == null)
                        {
                            return;
                        }
                        models = [prepared.Model];
                        publishGroupedEvents = prepared.PublishGroupedEvents;
                    }
                    await PrepareModelsForSaveAsync(
                        models,
                        existingEntities,
                        repository,
                        ct);
                    await BeforeSaveChanges(models);

                    // Load only the content explicitly supplied for update, so Patch changes the tracked
                    // table-split dependent rather than inserting a second one for the same page.
                    var contentPageIds = models.SelectMany(x => x.Pages ?? [])
                        .Where(x => x.Content != null && !string.IsNullOrEmpty(x.Id)).Select(x => x.Id).ToArray();
                    if (contentPageIds.Length > 0)
                    {
                        await repository.SiteBuilderContents.Where(x => contentPageIds.Contains(x.Id)).LoadAsync(ct);
                    }

                    ApplyEntityChanges(repository, models, existingEntities, primaryKeyMap, changedEntries, changedEntities, originalModels);

                    if (publishGroupedEvents)
                    {
                        groupedEventEntries.AddRange(changedEntries);
                        await _eventPublisher.Publish(EventFactory<GroupedSiteBuilderPageChangingEvent>(changedEntries), ct);
                    }
                    await CommitAsync(repository);
                    primaryKeyMap.ResolvePrimaryKeys();
                    await RebuildWrittenContentIndexesAsync(repository, models, ct);
                }

                await repository.ExecuteUnderGroupedPageWriteLocksAsync(
                    groupIds,
                    SaveInternalAsync,
                    cancellationToken);
            }

            if (changedEntries.All(x => x == null))
            {
                return;
            }

            ClearCache(originalModels);
            ClearCache(models);

            for (var index = 0; index < changedEntries.Length; index++)
            {
                var changedEntry = changedEntries[index];
                changedEntry.NewEntry = ToModel(changedEntities[index], changedEntry.NewEntry);
            }

            await AfterSaveChangesAsync(models, changedEntries);
            await PublishGroupedChangedEventAsync(groupedEventEntries);
        }

        private Task PublishGroupedChangedEventAsync(List<GenericChangedEntry<GroupedSiteBuilderPage>> entries)
        {
            return entries.Count == 0
                ? Task.CompletedTask
                : _eventPublisher.Publish(EventFactory<GroupedSiteBuilderPageChangedEvent>(entries), CancellationToken.None);
        }

        private void ApplyEntityChanges(
            ISiteBuilderRepository repository, IList<GroupedSiteBuilderPage> models,
            IList<GroupedSiteBuilderPageEntity> existingEntities, PrimaryKeyResolvingMap primaryKeyMap,
            GenericChangedEntry<GroupedSiteBuilderPage>[] changedEntries, GroupedSiteBuilderPageEntity[] changedEntities,
            List<GroupedSiteBuilderPage> originalModels)
        {
            for (var index = 0; index < models.Count; index++)
            {
                var model = models[index];
                var originalEntity = FindExistingEntity(existingEntities, model);
                var modifiedEntity = FromModel(model, primaryKeyMap);
                if (originalEntity != null)
                {
                    repository.TrackModifiedAsAddedForNewChildEntities(originalEntity);
                    var originalModel = ToModel(originalEntity, model: null);
                    originalModels.Add(originalModel);
                    changedEntries[index] = new GenericChangedEntry<GroupedSiteBuilderPage>(model, originalModel, EntryState.Modified);
                    modifiedEntity.Patch(originalEntity);
                    originalEntity.ModifiedDate = DateTime.UtcNow;
                    changedEntities[index] = originalEntity;
                }
                else
                {
                    repository.Add(modifiedEntity);
                    changedEntries[index] = new GenericChangedEntry<GroupedSiteBuilderPage>(model, EntryState.Added);
                    changedEntities[index] = modifiedEntity;
                }
            }
        }

        private static async Task RebuildWrittenContentIndexesAsync(
            ISiteBuilderRepository repository, IList<GroupedSiteBuilderPage> models, CancellationToken cancellationToken)
        {
            foreach (var group in models)
            {
                foreach (var page in (group.Pages ?? []).Where(x => x.Content != null))
                {
                    if (repository is ISiteBuilderContentIndexRepository indexes)
                    {
                        await indexes.RebuildPageContentIndexesAsync(page.Id, page.Content, group.StoreId, cancellationToken);
                    }
                    else
                    {
                        await SiteBuilderPageIndexing.RebuildAfterRawContentWriteAsync(repository, page.Id, page.Content, group.StoreId, cancellationToken);
                    }
                }
            }
        }

        protected override async Task<IList<GroupedSiteBuilderPageEntity>> LoadEntities(IRepository repository, IList<string> ids, string responseGroup)
        {
            var result = await ((ISiteBuilderRepository)repository).GetGroupedSiteBuilderPagesByIdsAsync(ids, responseGroup);
            return result;
        }

        private async Task PrepareModelsForSaveAsync(
            IList<GroupedSiteBuilderPage> models,
            IList<GroupedSiteBuilderPageEntity> existingEntities,
            ISiteBuilderRepository repository,
            CancellationToken cancellationToken)
        {
            var existingStores = existingEntities
                .ToDictionary(x => x.Id, x => x.StoreId, StringComparer.OrdinalIgnoreCase);
            var groupsWithSharedComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = existingStores.Keys.ToArray();

            if (ids.Length > 0)
            {
                foreach (var batch in ids.Chunk(ExistingGroupsQueryBatchSize))
                {
                    var referencedGroupIds = await repository.SiteBuilderPages
                        .Where(x => batch.Contains(x.GroupId))
                        .Join(
                            repository.SiteBuilderSharedComponentReferences,
                            page => page.Id,
                            reference => reference.PageId,
                            (page, reference) => page.GroupId)
                        .Distinct()
                        .ToListAsync(cancellationToken);
                    groupsWithSharedComponents.UnionWith(referencedGroupIds);
                }
            }

            ValidateStoreImmutability(models, existingStores, groupsWithSharedComponents);
            SynchronizeMovedPageStores(models, existingStores);

            foreach (var group in models)
            {
                var existingPages = existingEntities
                    .FirstOrDefault(x => string.Equals(x.Id, group.Id, StringComparison.OrdinalIgnoreCase))
                    ?.Pages;
                NormalizePublishedPages(group, existingPages);
            }

            await SynchronizeLanguageVersionsAsync(models, existingEntities, repository, cancellationToken);
        }

        // A language version shares the root name, and the fields it overrides are exactly the fields that differ
        // from the root. Deriving the mask on every save keeps it right for any writer (the page blade, import,
        // root propagation), and a field set back to the root value becomes inherited again.
        private static async Task SynchronizeLanguageVersionsAsync(
            IList<GroupedSiteBuilderPage> models,
            IList<GroupedSiteBuilderPageEntity> existingEntities,
            ISiteBuilderRepository repository,
            CancellationToken cancellationToken)
        {
            // The link is never patched, so the stored value wins over the model for existing groups.
            var rootIdsByGroup = models.ToDictionary(
                x => x,
                x => existingEntities.FirstOrDefault(e => string.Equals(e.Id, x.Id, StringComparison.OrdinalIgnoreCase)) is { } existing
                    ? existing.MultilanguagePageId
                    : x.MultilanguagePageId);
            var rootIds = rootIdsByGroup.Values.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToArray();
            if (rootIds.Length == 0)
            {
                return;
            }

            var roots = await repository.MultilanguagePages.AsNoTracking()
                .Where(x => rootIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.ToModel(AbstractTypeFactory<MultilanguagePage>.TryCreateInstance()), cancellationToken);

            await ValidateLanguageVersionsAsync(rootIdsByGroup, roots, repository, cancellationToken);

            foreach (var (group, rootId) in rootIdsByGroup)
            {
                if (rootId != null && roots.TryGetValue(rootId, out var root))
                {
                    group.Name = root.Name;
                    MultilanguagePageInheritance.SetOverriddenFields(group, MultilanguagePageInheritance.GetDifferingFields(root, group));

                    if (IsBeingPublished(group, existingEntities))
                    {
                        group.SourceRevisionDate = await GetPublishedSourceRevisionAsync(group, root, repository, cancellationToken);
                    }
                }
            }
        }

        // A language version stays in the store of its root and is the only version of its language there,
        // whichever writer saves it (the page blade, import, a client sending a link with a new page).
        private static async Task ValidateLanguageVersionsAsync(
            IDictionary<GroupedSiteBuilderPage, string> rootIdsByGroup,
            Dictionary<string, MultilanguagePage> roots,
            ISiteBuilderRepository repository,
            CancellationToken cancellationToken)
        {
            var linkedGroups = rootIdsByGroup.Where(x => !string.IsNullOrEmpty(x.Value)).ToList();
            var rootIds = linkedGroups.Select(x => x.Value).Distinct().ToArray();
            var storedVersions = await repository.GroupedSiteBuilderPages.AsNoTracking()
                .Where(x => rootIds.Contains(x.MultilanguagePageId))
                .Select(x => new { x.Id, x.MultilanguagePageId, x.CultureName })
                .ToListAsync(cancellationToken);
            var savedIds = linkedGroups.Select(x => x.Key.Id).Where(x => x != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var versions = storedVersions
                .Where(x => !savedIds.Contains(x.Id))
                .Select(x => (x.MultilanguagePageId, x.CultureName))
                .ToList();

            foreach (var (group, rootId) in linkedGroups)
            {
                if (!roots.TryGetValue(rootId, out var root))
                {
                    throw new InvalidDataException($"Multilanguage page '{rootId}' not found.");
                }

                if (!string.Equals(group.StoreId, root.StoreId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"A language version of multilanguage page '{rootId}' must belong to store '{root.StoreId}'.");
                }

                if (string.IsNullOrWhiteSpace(group.CultureName))
                {
                    throw new InvalidDataException($"A language version of multilanguage page '{rootId}' must have a language.");
                }

                if (versions.Any(x => x.MultilanguagePageId == rootId && string.Equals(x.CultureName, group.CultureName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"Multilanguage page '{rootId}' already has a '{group.CultureName}' version.");
                }

                versions.Add((rootId, group.CultureName));
            }
        }

        // Publishing the source language starts a new revision; publishing a translation marks it as based on the current one.
        private static async Task<DateTime?> GetPublishedSourceRevisionAsync(
            GroupedSiteBuilderPage group,
            MultilanguagePage root,
            ISiteBuilderRepository repository,
            CancellationToken cancellationToken)
        {
            if (string.Equals(group.CultureName, root.DefaultCultureName, StringComparison.OrdinalIgnoreCase))
            {
                return DateTime.UtcNow;
            }

            return await repository.GroupedSiteBuilderPages.AsNoTracking()
                .Where(x => x.MultilanguagePageId == root.Id && x.CultureName == root.DefaultCultureName)
                .Select(x => x.SourceRevisionDate)
                .FirstOrDefaultAsync(cancellationToken);
        }

        private static bool IsBeingPublished(GroupedSiteBuilderPage group, IList<GroupedSiteBuilderPageEntity> existingEntities)
        {
            var publishedBefore = existingEntities
                .FirstOrDefault(x => string.Equals(x.Id, group.Id, StringComparison.OrdinalIgnoreCase))?.Pages
                .Where(x => x.Status == Published)
                .Select(x => x.Id)
                .ToHashSet() ?? [];

            return (group.Pages ?? []).Any(x => x.Status == Published && !publishedBefore.Contains(x.Id));
        }

        internal static void ValidateStoreImmutability(
            IEnumerable<GroupedSiteBuilderPage> groups,
            IReadOnlyDictionary<string, string> existingStores,
            ISet<string> groupsWithSharedComponents)
        {
            foreach (var group in groups.Where(x => !string.IsNullOrWhiteSpace(x.Id)))
            {
                if (existingStores.TryGetValue(group.Id, out var existingStoreId) &&
                    groupsWithSharedComponents.Contains(group.Id) &&
                    !string.Equals(existingStoreId, group.StoreId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Page group '{group.Id}' cannot be moved from store '{existingStoreId}' to '{group.StoreId}'.");
                }
            }
        }

        internal static void SynchronizeMovedPageStores(
            IEnumerable<GroupedSiteBuilderPage> groups,
            IReadOnlyDictionary<string, string> existingStores)
        {
            foreach (var group in groups.Where(x => !string.IsNullOrWhiteSpace(x.Id)))
            {
                if (!existingStores.TryGetValue(group.Id, out var existingStoreId) ||
                    string.Equals(existingStoreId, group.StoreId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var page in group.Pages ?? [])
                {
                    page.StoreId = group.StoreId;
                }
            }
        }

        // A grouped save changes the status of child pages (publish/unpublish/archive), but the base
        // ClearCache only expires GroupedSiteBuilderPage regions. Read paths that hydrate individual
        // pages — notably SiteBuilderPageSearchService used by the search-index reindex provider —
        // go through SiteBuilderPage caches, which would otherwise keep serving the pre-change status
        // until they expire. That makes a reindex write stale statuses to the index (the event-driven
        // path is unaffected because it reads the freshly saved entry, not the cache). Invalidate the
        // affected pages' caches here so a reindex immediately reflects the new status.
        protected override void ClearCache(IList<GroupedSiteBuilderPage> models)
        {
            base.ClearCache(models);

            var pageIds = models
                .Where(x => x?.Pages != null)
                .SelectMany(x => x.Pages)
                .Select(x => x.Id)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            if (pageIds.Count == 0)
            {
                return;
            }

            foreach (var id in pageIds)
            {
                GenericCachingRegion<SiteBuilderPage>.ExpireTokenForKey(id);
            }

            GenericSearchCachingRegion<SiteBuilderPage>.ExpireRegion();
        }

        // Enforces invariant: at most one Published page per group.
        // If multiple Published exist, picks the page that is transitioning to Published in this save
        // (compared against DB state) and demotes the rest to Archived. This handles the legitimate
        // PublishGroup flow silently. If no clear transition exists (data anomaly from import/migration),
        // falls back to "newest by CreatedDate" and logs a warning.
        // Reference queries read page status from SiteBuilderPage, so demoted pages do not need reference metadata refresh.
        private void NormalizePublishedPages(
            GroupedSiteBuilderPage group,
            IEnumerable<SiteBuilderPageEntity> existingPages)
        {
            if (group?.Pages == null)
            {
                return;
            }

            var publishedPages = group.Pages.Where(x => x.Status == Published).ToList();
            if (publishedPages.Count <= 1)
            {
                return;
            }

            var keep = FindNewlyPromotedPage(group.Id, publishedPages, existingPages);

            if (keep == null)
            {
                keep = SelectFallbackPublishedPage(group.Id, publishedPages);
            }

            ArchiveOtherPublishedPages(publishedPages, keep.Id);
        }

        private static SiteBuilderPage FindNewlyPromotedPage(
            string groupId,
            IEnumerable<SiteBuilderPage> publishedPages,
            IEnumerable<SiteBuilderPageEntity> existingPages)
        {
            if (string.IsNullOrEmpty(groupId) || existingPages == null)
            {
                return null;
            }

            var existingStatusById = existingPages
                .Where(page => !string.IsNullOrEmpty(page.Id))
                .ToDictionary(page => page.Id, page => page.Status);

            var newlyPromoted = publishedPages
                .Where(page => !string.IsNullOrEmpty(page.Id)
                    && existingStatusById.TryGetValue(page.Id, out var oldStatus)
                    && oldStatus != Published)
                .Take(2)
                .ToList();

            return newlyPromoted.Count == 1 ? newlyPromoted[0] : null;
        }

        private SiteBuilderPage SelectFallbackPublishedPage(
            string groupId,
            IEnumerable<SiteBuilderPage> publishedPages)
        {
            var keep = publishedPages.OrderByDescending(page => page.CreatedDate).First();
            _logger.LogWarning(
                "Group '{GroupId}' has multiple Published pages without a clear status transition. " +
                "Keeping page '{KeepId}' (newest CreatedDate) and demoting the rest to Archived.",
                groupId,
                keep.Id);
            return keep;
        }

        private static void ArchiveOtherPublishedPages(
            IEnumerable<SiteBuilderPage> publishedPages,
            string keepId)
        {
            foreach (var page in publishedPages.Where(page => page.Id != keepId))
            {
                page.Status = Archived;
            }
        }

        public async Task<string> LoadContent(string pageId, CancellationToken cancellationToken = default)
        {
            await using var memoryStream = new MemoryStream();
            await LoadContentToStreamAsync(pageId, memoryStream, cancellationToken);
            memoryStream.Position = 0;
            using var reader = new StreamReader(memoryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        public async Task SaveContent(string pageId, string content, CancellationToken cancellationToken = default)
        {
            await using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await SaveStreamAsContentAsync(pageId, memoryStream, cancellationToken);
        }

        public virtual async Task<SiteBuilderConditionalContentWriteResult> SaveGroupContentAsync(
            GroupedSiteBuilderPage authorizedGroup, string content, string expectedETag,
            CancellationToken cancellationToken = default)
        {
            GroupedSiteBuilderPage savedGroup = authorizedGroup;
            string pageId = null;
            var contentWritten = false;
            var groupedEventsPublished = false;
            await SaveChangesInternalAsync([authorizedGroup], async (repository, entities, ct) =>
            {
                var current = GetAuthorizedGroup(authorizedGroup, entities);
                savedGroup = current;
                var snapshot = await ReadContentAsync(repository, current, draft: true, ct);
                if (!string.Equals(snapshot.ETag, expectedETag, StringComparison.Ordinal))
                {
                    // A response can be lost after commit. Retrying the identical document is harmless;
                    // a different document must still prove it was based on the current version.
                    if (!string.Equals(snapshot.Content, content, StringComparison.Ordinal))
                    {
                        throw new SiteBuilderContentConflictException();
                    }
                    pageId = snapshot.PageId;
                    return null;
                }

                groupedEventsPublished = !current.Pages.Any(x => x.Status == Draft);
                var target = GetOrAddDraft(current);
                target.Content = content;
                pageId = target.Id;
                contentWritten = true;
                return new PreparedSave(current, groupedEventsPublished);
            }, cancellationToken);

            // The token depends only on the locked group's identity and the submitted document.
            // Hashing the next version does not need to keep database locks held.
            return new(pageId, SiteBuilderContentVersion.Create(savedGroup, content))
            {
                ContentWritten = contentWritten,
                GroupedEventsPublished = groupedEventsPublished,
            };
        }

        public virtual async Task<SiteBuilderContentSnapshot> LoadGroupContentAsync(
            GroupedSiteBuilderPage authorizedGroup, bool draft = true, CancellationToken cancellationToken = default)
        {
            using var repository = _repositoryFactory();
            var entities = await repository.GetGroupedSiteBuilderPagesByIdsAsync([authorizedGroup.Id], null);
            var current = GetAuthorizedGroup(authorizedGroup, entities);
            return await ReadContentAsync(repository, current, draft, cancellationToken);
        }

        public virtual Task SaveGroupAtVersionAsync(GroupedSiteBuilderPage group, bool draft, string expectedETag,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(group);

            return SaveChangesInternalAsync([group], async (repository, entities, ct) =>
            {
                // read under the lock of the save, so that the version compared is the one the save replaces
                var entity = entities.SingleOrDefault();
                var current = entity == null ? null : await ReadContentAsync(repository, ToModel(entity, null), draft, ct);
                if (!string.Equals(current?.ETag, expectedETag, StringComparison.Ordinal))
                {
                    throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.PageChangedMessage);
                }
                return new PreparedSave(group);
            }, cancellationToken);
        }

        public virtual Task SaveGroupSettingsAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken = default)
        {
            return SaveChangesInternalAsync([group], async (repository, entities, ct) =>
            {
                var existing = entities.SingleOrDefault();
                var settingsChanged = HasContentSettingsChanged(existing, group);
                if (existing != null)
                {
                    if (existing.CreatedDate != group.CreatedDate)
                    {
                        throw new KeyNotFoundException("The authorized page group no longer exists.");
                    }
                    // Membership is read under the group lock; a cached Shell model cannot hide a new draft.
                    group.Pages = ToModel(existing, null).Pages;
                }
                // An archived page keeps its settings in sync but gets no draft: a draft would bring it back to Draft.
                if (IsArchived(group))
                {
                    return new PreparedSave(group);
                }
                var snapshot = await ReadContentAsync(repository, group, draft: true, ct);
                if (!settingsChanged && snapshot.Content != null)
                {
                    return new PreparedSave(group);
                }
                var updated = SiteBuilderContentSettings.Synchronize(snapshot.Content, group);
                if (!string.Equals(updated, snapshot.Content, StringComparison.Ordinal))
                {
                    GetOrAddDraft(group).Content = updated;
                }
                return new PreparedSave(group);
            }, cancellationToken);
        }

        // The settings the page content JSON repeats.
        private static bool HasContentSettingsChanged(GroupedSiteBuilderPageEntity existing, GroupedSiteBuilderPage group)
        {
            return existing == null || existing.Name != group.Name
                || existing.Permalink != group.Permalink || existing.CultureName != group.CultureName;
        }

        private static bool IsArchived(GroupedSiteBuilderPage group)
        {
            return group.Pages.Count > 0 && group.Pages.All(x => x.Status == Archived);
        }

        private GroupedSiteBuilderPage GetAuthorizedGroup(
            GroupedSiteBuilderPage authorizedGroup, IList<GroupedSiteBuilderPageEntity> entities)
        {
            var entity = entities.SingleOrDefault();
            if (entity == null || !string.Equals(entity.StoreId, authorizedGroup.StoreId, StringComparison.OrdinalIgnoreCase)
                || entity.CreatedDate != authorizedGroup.CreatedDate)
            {
                throw new KeyNotFoundException("The authorized page group no longer exists.");
            }
            return ToModel(entity, null);
        }

        private static async Task<SiteBuilderContentSnapshot> ReadContentAsync(
            ISiteBuilderRepository repository, GroupedSiteBuilderPage group, bool draft, CancellationToken cancellationToken)
        {
            foreach (var pageId in SiteBuilderPageSelection.Order(group.Pages, draft).Select(page => page.Id))
            {
                var content = await repository.SiteBuilderContents.AsNoTracking()
                    .Where(x => x.Id == pageId).Select(x => x.PageContent).FirstOrDefaultAsync(cancellationToken);
                if (content != null)
                {
                    return new(pageId, content, SiteBuilderContentVersion.Create(group, content));
                }
            }
            return new(null, null, SiteBuilderContentVersion.Create(group, null));
        }

        private static SiteBuilderPage GetOrAddDraft(GroupedSiteBuilderPage group)
        {
            var draft = SiteBuilderPageSelection.Order(group.Pages).FirstOrDefault(x => x.Status == Draft);
            if (draft == null)
            {
                draft = AbstractTypeFactory<SiteBuilderPage>.TryCreateInstance();
                draft.Id = Guid.NewGuid().ToString("N");
                draft.GroupId = group.Id;
                draft.StoreId = group.StoreId;
                draft.Status = Draft;
                group.Pages.Add(draft);
            }
            return draft;
        }

        private sealed record PreparedSave(GroupedSiteBuilderPage Model, bool PublishGroupedEvents = true);

        public async Task<bool> LoadContentToStreamAsync(string pageId, Stream stream, CancellationToken cancellationToken = default)
        {
            await using var repository = _contentStreamRepositoryFactory();

            // Deliberately not disposed: disposing flushes, and flushing an HTTP response body commits the
            // status line, which would make the caller's fall-through to the next candidate — or to 404 —
            // impossible. It would also emit the UTF8 preamble for a page that turned out to have no content.
            // The writer holds no unmanaged resources and the stream is left open either way.
            var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 8192, leaveOpen: true);
            var found = await repository.TryLoadBinaryAsync(pageId, writer, cancellationToken);
            if (found)
            {
                await writer.FlushAsync(cancellationToken);
            }

            return found;
        }

        public async Task SaveStreamAsContentAsync(string pageId, Stream stream, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var content = await reader.ReadToEndAsync(cancellationToken);
            await using var repository = _contentStreamRepositoryFactory();
            await repository.SavePageContentAsync(
                pageId,
                content,
                cancellationToken);
        }

        public async Task CopyPageContentAsync(string sourcePageId, string targetPageId, CancellationToken cancellationToken = default)
        {
            await using var repository = _contentStreamRepositoryFactory();
            await repository.CopyPageContentAsync(
                sourcePageId,
                targetPageId,
                cancellationToken);
        }

        public async Task<bool> TryDeleteEmptyDraftAsync(
            string pageId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pageId);

            var deleted = false;
            var groupId = (string)null;
            using (var repository = _repositoryFactory())
            {
                await repository.ExecuteUnderPageWriteLocksAsync(
                    [pageId],
                    async transactionCancellationToken =>
                    {
                        // A successful concurrent content writer holds this same row lock. Rechecking after the
                        // lock prevents failed-request cleanup from deleting a draft another request filled.
                        var page = await repository.SiteBuilderPages
                            .FirstOrDefaultAsync(x => x.Id == pageId, transactionCancellationToken);
                        if (page == null || page.Status != Draft)
                        {
                            return;
                        }

                        var hasContent = await repository.SiteBuilderContents
                            .AnyAsync(
                                x => x.Id == pageId && x.PageContent != null,
                                transactionCancellationToken);
                        var hasSharedComponentReferences = await repository.SiteBuilderSharedComponentReferences
                            .AnyAsync(x => x.PageId == pageId, transactionCancellationToken);
                        if (hasContent || hasSharedComponentReferences)
                        {
                            return;
                        }

                        groupId = page.GroupId;
                        repository.Remove(page);
                        await repository.UnitOfWork.CommitAsync();
                        deleted = true;
                    },
                    cancellationToken);
            }

            if (deleted)
            {
                GenericCachingRegion<SiteBuilderPage>.ExpireTokenForKey(pageId);
                GenericSearchCachingRegion<SiteBuilderPage>.ExpireRegion();
                GenericSearchCachingRegion<GroupedSiteBuilderPage>.ExpireRegion();
                if (!string.IsNullOrWhiteSpace(groupId))
                {
                    GenericCachingRegion<GroupedSiteBuilderPage>.ExpireTokenForKey(groupId);
                }

                // This rollback path removes a draft that was already announced to Pages by the grouped save.
                // A normal SiteBuilder delete maps to Archive, so emit the explicit hard-delete operation here.
                var pageDocument = AbstractTypeFactory<PageDocument>.TryCreateInstance();
                pageDocument.Id = pageId;

                var pagesEvent = AbstractTypeFactory<PagesDomainEvent>.TryCreateInstance();
                pagesEvent.Id = pageId;
                pagesEvent.Page = pageDocument;
                pagesEvent.Operation = PageOperation.Delete;

                // The row deletion has already committed, so request cancellation must not suppress the
                // corresponding Pages delete notification and leave the downstream index stale.
                await _eventPublisher.Publish(pagesEvent, CancellationToken.None);
            }

            return deleted;
        }

    }
}
