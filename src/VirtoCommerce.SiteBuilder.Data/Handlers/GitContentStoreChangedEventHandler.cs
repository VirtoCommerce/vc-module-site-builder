using Microsoft.Extensions.Logging;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Data.BackgroundJobs;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.StoreModule.Core.Events;
using VirtoCommerce.StoreModule.Core.Model;

namespace VirtoCommerce.SiteBuilder.Data.Handlers;

public class GitContentStoreChangedEventHandler(
    IGitHubConnectionService gitHubConnectionService,
    IGitContentPolicy gitContentPolicy,
    ILogger<GitContentStoreChangedEventHandler> logger)
    : IEventHandler<StoreChangedEvent>
{
    private static readonly EnqueueOptions _enqueueOptions = new()
    {
        MaxRetryAttempts = GitContentRepositoryJob.MaxRetryAttempts,
    };

    public virtual async Task Handle(StoreChangedEvent message)
    {
        foreach (var payload in await GetJobPayloadsAsync(message))
        {
            try
            {
                // the handler is resolved once, from the root provider, so it must not capture a scoped job client
                await BackgroundJob.Enqueue<GitContentRepositoryJob>(payload, _enqueueOptions);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to enqueue the Git repository job for store {StoreId}", payload.StoreId);
            }
        }
    }

    protected virtual async Task<IList<GitContentRepositoryJobPayload>> GetJobPayloadsAsync(StoreChangedEvent message)
    {
        var payloads = new List<GitContentRepositoryJobPayload>();

        var connection = await gitHubConnectionService.GetConnectionAsync();
        if (!connection.IsEnabled || !connection.IsConfigured || string.IsNullOrWhiteSpace(connection.Organization))
        {
            return payloads;
        }

        foreach (var entry in message.ChangedEntries)
        {
            if (await GetJobPayloadAsync(entry, connection) is { } payload)
            {
                payloads.Add(payload);
            }
        }

        return payloads;
    }

    protected virtual async Task<StoreRepository> GetRepositoryOrDefaultAsync(string storeId, string repositorySetting)
    {
        try
        {
            return await gitContentPolicy.GetConfiguredRepositoryAsync(storeId, repositorySetting);
        }
        catch (GitContentException)
        {
            return null;
        }
    }

    private async Task<GitContentRepositoryJobPayload> GetJobPayloadAsync(GenericChangedEntry<Store> entry, GitHubConnection connection)
    {
        var store = entry.NewEntry ?? entry.OldEntry;
        if (string.IsNullOrEmpty(store?.Id))
        {
            return null;
        }

        if (entry.EntryState == EntryState.Added && connection.AutoCreateRepositories)
        {
            return CreatePayload(store.Id, GitContentRepositoryAction.Connect, repository: null);
        }

        if (entry.EntryState != EntryState.Deleted)
        {
            return null;
        }

        // a deleted store's settings exist only on the event
        var repositorySetting = store.Settings?.GetValue<string>(ModuleConstants.Settings.StoreLevelSettings.GitContentRepository);
        var repository = await GetRepositoryOrDefaultAsync(store.Id, repositorySetting);

        // never archive a repository named in the store setting
        return repository?.Source == GitRepositorySource.Site
            ? CreatePayload(store.Id, GitContentRepositoryAction.Archive, repository.Name)
            : null;
    }

    private static GitContentRepositoryJobPayload CreatePayload(string storeId, GitContentRepositoryAction action, string repository)
    {
        var payload = AbstractTypeFactory<GitContentRepositoryJobPayload>.TryCreateInstance();
        payload.StoreId = storeId;
        payload.Action = action;
        payload.Repository = repository;
        return payload;
    }
}
