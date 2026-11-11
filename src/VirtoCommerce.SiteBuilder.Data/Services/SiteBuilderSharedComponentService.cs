using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.Platform.Caching;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Data.GenericCrud;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class SiteBuilderSharedComponentService
    : CrudService<SiteBuilderSharedComponent, SiteBuilderSharedComponentEntity,
        SiteBuilderSharedComponentChangingEvent, SiteBuilderSharedComponentChangedEvent>,
      ISiteBuilderSharedComponentService
{
    private const int ExistingComponentsQueryBatchSize = 500;

    private readonly IEventPublisher _eventPublisher;
    private readonly ISiteBuilderSharedComponentAssetReferenceIndexService _assetReferenceIndexService;
    private readonly ILogger<SiteBuilderSharedComponentService> _logger;
    private readonly Func<ISiteBuilderRepository> _repositoryFactory;

    public SiteBuilderSharedComponentService(
        Func<ISiteBuilderRepository> repositoryFactory,
        IPlatformMemoryCache platformMemoryCache,
        IEventPublisher eventPublisher,
        ISiteBuilderSharedComponentAssetReferenceIndexService assetReferenceIndexService,
        ILogger<SiteBuilderSharedComponentService> logger = null)
        : base(repositoryFactory, platformMemoryCache, eventPublisher)
    {
        _repositoryFactory = repositoryFactory;
        _eventPublisher = eventPublisher;
        _assetReferenceIndexService = assetReferenceIndexService;
        _logger = logger;
    }

    public override Task SaveChangesAsync(IList<SiteBuilderSharedComponent> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (models.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (models.Any(model => string.IsNullOrWhiteSpace(model?.Id)))
        {
            throw new InvalidOperationException(
                "Shared Components must be created together with their content. Use SaveWithContentAsync.");
        }

        return SaveChangesInternalAsync(models);
    }

    private async Task SaveChangesInternalAsync(IList<SiteBuilderSharedComponent> models)
    {
        if (!await SaveMetadataBatchAsync(models, CancellationToken.None))
        {
            throw new KeyNotFoundException("One or more Shared Components were not found or were replaced.");
        }
    }

    public Task<SiteBuilderSharedComponent> UpdateMetadataAsync(
        SiteBuilderSharedComponent model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Id);

        return UpdateMetadataInternalAsync(model, cancellationToken);
    }

    private async Task<SiteBuilderSharedComponent> UpdateMetadataInternalAsync(
        SiteBuilderSharedComponent model,
        CancellationToken cancellationToken)
    {
        return await SaveMetadataBatchAsync([model], cancellationToken, validateStorePreflight: false)
            ? model
            : null;
    }

    private async Task<bool> SaveMetadataBatchAsync(
        IList<SiteBuilderSharedComponent> models,
        CancellationToken cancellationToken,
        bool validateStorePreflight = true)
    {
        await PrepareMetadataBatchAsync(models, validateStorePreflight);

        var ids = SiteBuilderWriteLock.OrderIds(models.Select(x => x.Id));
        if (ids.Length != models.Count)
        {
            throw new InvalidDataException("A metadata batch cannot contain duplicate Shared Component ids.");
        }

        var state = new MetadataBatchSaveState(models.Count);

        using (var repository = _repositoryFactory())
        {
            if (!await TryPersistMetadataBatchAsync(
                    repository,
                    models,
                    ids,
                    state,
                    cancellationToken))
            {
                return false;
            }
        }

        state.PrimaryKeyMap.ResolvePrimaryKeys();
        ClearCache(state.OriginalModels);
        ClearCache(models);

        UpdateChangedModels(state);

        await AfterSaveChangesAsync(models, state.ChangedEntries);
        await PublishAfterCommitAsync(
            EventFactory<SiteBuilderSharedComponentChangedEvent>(state.ChangedEntries),
            string.Join(", ", ids));

        return true;
    }

    private async Task PrepareMetadataBatchAsync(
        IList<SiteBuilderSharedComponent> models,
        bool validateStorePreflight)
    {
        if (validateStorePreflight)
        {
            await BeforeSaveChanges(models);
            return;
        }

        foreach (var model in models)
        {
            ValidateAndNormalize(model);
        }

        await base.BeforeSaveChanges(models);
    }

    private async Task<bool> TryPersistMetadataBatchAsync(
        ISiteBuilderRepository repository,
        IList<SiteBuilderSharedComponent> models,
        string[] ids,
        MetadataBatchSaveState state,
        CancellationToken cancellationToken)
    {
        var allComponentsExist = await repository.ExecuteUnderSharedComponentWriteLocksAsync(
            ids,
            async transactionCancellationToken =>
            {
                var entities = await LoadSharedComponentEntitiesAsync(
                    repository,
                    ids,
                    transactionCancellationToken);
                var entitiesById = entities.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
                var contentIds = await LoadSharedComponentContentIdsAsync(
                    repository,
                    ids,
                    transactionCancellationToken);

                if (!AllAggregatesMatch(models, entitiesById, contentIds))
                {
                    return;
                }

                TrackMetadataChanges(models, entitiesById, state);
                await _eventPublisher.Publish(
                    EventFactory<SiteBuilderSharedComponentChangingEvent>(state.ChangedEntries),
                    transactionCancellationToken);
                await repository.UnitOfWork.CommitAsync();
                state.Saved = true;
            },
            cancellationToken);

        return allComponentsExist && state.Saved;
    }

    private static bool AllAggregatesMatch(
        IEnumerable<SiteBuilderSharedComponent> models,
        Dictionary<string, SiteBuilderSharedComponentEntity> entitiesById,
        HashSet<string> contentIds)
    {
        return models.All(model =>
            entitiesById.TryGetValue(model.Id, out var entity) &&
            contentIds.Contains(model.Id) &&
            HasExpectedIdentity(entity, model));
    }

    private void TrackMetadataChanges(
        IEnumerable<SiteBuilderSharedComponent> models,
        Dictionary<string, SiteBuilderSharedComponentEntity> entitiesById,
        MetadataBatchSaveState state)
    {
        foreach (var model in models)
        {
            var originalEntity = entitiesById[model.Id];
            var originalModel = ToModel(originalEntity, model: null);
            var modifiedEntity = FromModel(model, state.PrimaryKeyMap);

            state.OriginalModels.Add(originalModel);
            state.ChangedEntries.Add(new GenericChangedEntry<SiteBuilderSharedComponent>(
                model,
                originalModel,
                EntryState.Modified));
            modifiedEntity.Patch(originalEntity);
            originalEntity.ModifiedDate = DateTime.UtcNow;
            state.ChangedEntities.Add(originalEntity);
        }
    }

    private void UpdateChangedModels(MetadataBatchSaveState state)
    {
        for (var index = 0; index < state.ChangedEntries.Count; index++)
        {
            var changedEntry = state.ChangedEntries[index];
            changedEntry.NewEntry = ToModel(state.ChangedEntities[index], changedEntry.NewEntry);
        }
    }

    public Task<bool> TryDeleteAsync(
        SiteBuilderSharedComponent expectedComponent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedComponent);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedComponent.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedComponent.StoreId);

        return TryDeleteInternalAsync(expectedComponent, cancellationToken);
    }

    private async Task<bool> TryDeleteInternalAsync(
        SiteBuilderSharedComponent expectedComponent,
        CancellationToken cancellationToken)
    {
        SiteBuilderSharedComponent deletedModel = null;
        GenericChangedEntry<SiteBuilderSharedComponent> changedEntry = null;

        using (var repository = _repositoryFactory())
        {
            var componentExists = await repository.ExecuteUnderSharedComponentWriteLockAsync(
                expectedComponent.Id,
                async transactionCancellationToken =>
                {
                    var entity = await repository.SiteBuilderSharedComponents
                        .FirstAsync(x => x.Id == expectedComponent.Id, transactionCancellationToken);
                    var contentExists = await repository.SiteBuilderSharedComponentContents
                        .AnyAsync(x => x.Id == expectedComponent.Id, transactionCancellationToken);

                    if (!contentExists ||
                        !string.Equals(entity.StoreId, expectedComponent.StoreId, StringComparison.OrdinalIgnoreCase) ||
                        entity.CreatedDate != expectedComponent.CreatedDate)
                    {
                        return;
                    }

                    deletedModel = ToModel(entity, model: null);
                    changedEntry = new GenericChangedEntry<SiteBuilderSharedComponent>(
                        deletedModel,
                        EntryState.Deleted);
                    await _eventPublisher.Publish(
                        EventFactory<SiteBuilderSharedComponentChangingEvent>([changedEntry]),
                        transactionCancellationToken);

                    repository.Remove(entity);
                    await repository.UnitOfWork.CommitAsync();
                },
                cancellationToken);

            if (!componentExists || deletedModel == null)
            {
                return false;
            }
        }

        ClearCache([deletedModel]);
        await AfterDeleteAsync([deletedModel], [changedEntry]);
        await PublishAfterCommitAsync(
            EventFactory<SiteBuilderSharedComponentChangedEvent>([changedEntry]),
            deletedModel.Id);

        return true;
    }

    public Task SaveWithContentAsync(
        SiteBuilderSharedComponent model,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        SiteBuilderSharedComponentReferenceMatcher.ValidateComponentContent(content);

        return SaveWithContentInternalAsync(model, content, cancellationToken);
    }

    private async Task SaveWithContentInternalAsync(
        SiteBuilderSharedComponent model,
        string content,
        CancellationToken cancellationToken)
    {
        var state = new SharedComponentSaveState(model, content);
        await BeforeSaveChanges(state.Models);

        using (var repository = _repositoryFactory())
        {
            await PersistAggregateUnderWriteLockAsync(
                repository,
                state,
                cancellationToken);
        }

        state.PrimaryKeyMap.ResolvePrimaryKeys();
        ClearCache(state.OriginalModels);
        ClearCache(state.Models);

        GenericSearchCachingRegion<SiteBuilderPage>.ExpireRegion();

        state.ChangedEntry.NewEntry = ToModel(state.ChangedEntity, state.ChangedEntry.NewEntry);
        await AfterSaveChangesAsync(state.Models, [state.ChangedEntry]);

        await PublishChangedEventsAsync(state);
    }

    private async Task PersistAggregateUnderWriteLockAsync(
        ISiteBuilderRepository repository,
        SharedComponentSaveState state,
        CancellationToken cancellationToken)
    {
        var existingComponentSaved = false;
        var existingComponentMatched = false;
        if (!string.IsNullOrWhiteSpace(state.Model.Id))
        {
            existingComponentSaved = await repository.ExecuteUnderSharedComponentWriteLockAsync(
                state.Model.Id,
                async transactionCancellationToken =>
                {
                    var existingEntities = await LoadExistingEntities(repository, state.Models);
                    var originalEntity = FindExistingEntity(existingEntities, state.Model);
                    if (!HasExpectedIdentity(originalEntity, state.Model))
                    {
                        return;
                    }

                    existingComponentMatched = true;
                    await PersistAggregateAsync(repository, originalEntity, state, transactionCancellationToken);
                },
                cancellationToken);
        }

        if (existingComponentSaved && !existingComponentMatched)
        {
            throw new DbUpdateConcurrencyException(
                $"Shared Component '{state.Model.Id}' was replaced while it was being saved.");
        }

        if (!existingComponentSaved)
        {
            if (state.Model.CreatedDate != default)
            {
                throw new DbUpdateConcurrencyException(
                    $"Shared Component '{state.Model.Id}' was deleted while it was being saved.");
            }

            await PersistAggregateAsync(repository, originalEntity: null, state, cancellationToken);
        }
    }

    private async Task PersistAggregateAsync(
        ISiteBuilderRepository repository,
        SiteBuilderSharedComponentEntity originalEntity,
        SharedComponentSaveState state,
        CancellationToken cancellationToken)
    {
        var changedEntity = TrackChangedEntity(repository, originalEntity, state);
        await UpsertContentAsync(repository, changedEntity.Id, state.Content, cancellationToken);

        await _assetReferenceIndexService.RebuildIndexInCurrentUnitOfWorkAsync(
            repository,
            changedEntity.Id,
            state.Content,
            cancellationToken);
        var changingEvent = EventFactory<SiteBuilderSharedComponentChangingEvent>([state.ChangedEntry]);
        await _eventPublisher.Publish(changingEvent, cancellationToken);
        await repository.UnitOfWork.CommitAsync();
    }

    private SiteBuilderSharedComponentEntity TrackChangedEntity(
        ISiteBuilderRepository repository,
        SiteBuilderSharedComponentEntity originalEntity,
        SharedComponentSaveState state)
    {
        var modifiedEntity = FromModel(state.Model, state.PrimaryKeyMap);
        if (originalEntity == null)
        {
            // The platform normally generates string keys when an entity is tracked. Assigning the same
            // compact GUID explicitly lets the required content child and its asset references share the
            // aggregate key before the single database commit.
            modifiedEntity.Id ??= Guid.NewGuid().ToString("N");
            repository.Add(modifiedEntity);
            state.ChangedEntity = modifiedEntity;
            state.ChangedEntry = new GenericChangedEntry<SiteBuilderSharedComponent>(state.Model, EntryState.Added);
        }
        else
        {
            var originalModel = ToModel(originalEntity, model: null);
            state.OriginalModels.Add(originalModel);
            state.ChangedEntry = new GenericChangedEntry<SiteBuilderSharedComponent>(state.Model, originalModel, EntryState.Modified);
            modifiedEntity.Patch(originalEntity);
            originalEntity.ModifiedDate = DateTime.UtcNow;
            state.ChangedEntity = originalEntity;
        }

        return state.ChangedEntity;
    }

    private static async Task UpsertContentAsync(
        ISiteBuilderRepository repository,
        string componentId,
        string content,
        CancellationToken cancellationToken)
    {
        var contentEntity = await repository.SiteBuilderSharedComponentContents
            .FirstOrDefaultAsync(x => x.Id == componentId, cancellationToken);

        if (contentEntity == null)
        {
            repository.Add(new SiteBuilderSharedComponentContentEntity
            {
                Id = componentId,
                ComponentContent = content,
            });
        }
        else
        {
            contentEntity.ComponentContent = content;
            repository.Update(contentEntity);
        }
    }

    private async Task PublishChangedEventsAsync(SharedComponentSaveState state)
    {
        var changedEvent = EventFactory<SiteBuilderSharedComponentChangedEvent>([state.ChangedEntry]);
        await PublishAfterCommitAsync(changedEvent, state.Model.Id);
        await PublishAfterCommitAsync(
            new SiteBuilderSharedComponentContentChangedEvent([state.Model.Id]),
            state.Model.Id);
    }

    private async Task PublishAfterCommitAsync<TEvent>(TEvent @event, string sharedComponentId)
        where TEvent : IEvent
    {
        try
        {
            await _eventPublisher.Publish(@event, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Failed to publish post-commit {EventType} for Shared Component {SharedComponentId}",
                typeof(TEvent).Name,
                sharedComponentId);
        }
    }

    protected override Task<IList<SiteBuilderSharedComponentEntity>> LoadEntities(
        IRepository repository,
        IList<string> ids,
        string responseGroup)
    {
        return ((ISiteBuilderRepository)repository)
            .GetSiteBuilderSharedComponentsByIdsAsync(ids, responseGroup);
    }

    protected override async Task BeforeSaveChanges(IList<SiteBuilderSharedComponent> models)
    {
        foreach (var model in models)
        {
            ValidateAndNormalize(model);
        }

        var ids = models
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var existingStores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (ids.Length > 0)
        {
            using var repository = _repositoryFactory();
            foreach (var batch in ids.Chunk(ExistingComponentsQueryBatchSize))
            {
                var existing = await repository.SiteBuilderSharedComponents
                    .Where(x => batch.Contains(x.Id))
                    .Select(x => new { x.Id, x.StoreId })
                    .ToListAsync();

                foreach (var item in existing)
                {
                    existingStores[item.Id] = item.StoreId;
                }
            }
        }

        ValidateStoreImmutability(models, existingStores);
        await base.BeforeSaveChanges(models);
    }

    internal static void ValidateAndNormalize(SiteBuilderSharedComponent model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model.StoreId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Name);

        model.Name = model.Name.Trim();
        if (model.Name.Length > ModuleConstants.SharedComponents.NameMaxLength)
        {
            throw new InvalidDataException(
                $"Shared Component name cannot exceed {ModuleConstants.SharedComponents.NameMaxLength} characters.");
        }
    }

    internal static void ValidateStoreImmutability(
        IEnumerable<SiteBuilderSharedComponent> models,
        IReadOnlyDictionary<string, string> existingStores)
    {
        foreach (var model in models.Where(x => !string.IsNullOrWhiteSpace(x.Id)))
        {
            if (existingStores.TryGetValue(model.Id, out var existingStoreId) &&
                !string.Equals(existingStoreId, model.StoreId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Shared Component '{model.Id}' cannot be moved from store '{existingStoreId}' to '{model.StoreId}'.");
            }
        }
    }

    private static bool HasExpectedIdentity(
        SiteBuilderSharedComponentEntity entity,
        SiteBuilderSharedComponent model)
    {
        return entity != null &&
               string.Equals(entity.StoreId, model.StoreId, StringComparison.OrdinalIgnoreCase) &&
               (model.CreatedDate == default || entity.CreatedDate == model.CreatedDate);
    }

    private static async Task<List<SiteBuilderSharedComponentEntity>> LoadSharedComponentEntitiesAsync(
        ISiteBuilderRepository repository,
        IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var result = new List<SiteBuilderSharedComponentEntity>();
        foreach (var batch in ids.Chunk(ExistingComponentsQueryBatchSize))
        {
            result.AddRange(await repository.SiteBuilderSharedComponents
                .Where(x => batch.Contains(x.Id))
                .ToListAsync(cancellationToken));
        }

        return result;
    }

    private static async Task<HashSet<string>> LoadSharedComponentContentIdsAsync(
        ISiteBuilderRepository repository,
        IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ids.Chunk(ExistingComponentsQueryBatchSize))
        {
            result.UnionWith(await repository.SiteBuilderSharedComponentContents
                .Where(x => batch.Contains(x.Id))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken));
        }

        return result;
    }

    private sealed class SharedComponentSaveState
    {
        public SharedComponentSaveState(SiteBuilderSharedComponent model, string content)
        {
            Model = model;
            Content = content;
            Models = [model];
        }

        public SiteBuilderSharedComponent Model { get; }
        public string Content { get; }
        public List<SiteBuilderSharedComponent> Models { get; }
        public List<SiteBuilderSharedComponent> OriginalModels { get; } = [];
        public PrimaryKeyResolvingMap PrimaryKeyMap { get; } = new();
        public SiteBuilderSharedComponentEntity ChangedEntity { get; set; }
        public GenericChangedEntry<SiteBuilderSharedComponent> ChangedEntry { get; set; }
    }

    private sealed class MetadataBatchSaveState(int capacity)
    {
        public List<SiteBuilderSharedComponent> OriginalModels { get; } = new(capacity);
        public List<GenericChangedEntry<SiteBuilderSharedComponent>> ChangedEntries { get; } = new(capacity);
        public List<SiteBuilderSharedComponentEntity> ChangedEntities { get; } = new(capacity);
        public PrimaryKeyResolvingMap PrimaryKeyMap { get; } = new();
        public bool Saved { get; set; }
    }
}
