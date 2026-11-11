using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.BackgroundJobs;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.Platform.Core.Settings;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class SiteBuilderAssetReferenceMigrationService(
    Func<ISiteBuilderRepository> repositoryFactory,
    ISettingsManager settingsManager,
    IBackgroundJob backgroundJob)
    : ISiteBuilderAssetReferenceMigrationService
{
    private const int _batchSize = 50;
    private static readonly object LockObject = new();

    public void StartMigration()
    {
        lock (LockObject)
        {
            var pageMigrationCompleted = settingsManager.GetValue<bool>(Settings.Migration.AssetReferenceIndexMigrated);
            if (!pageMigrationCompleted)
            {
                // Waits for the enqueue: StartMigration runs synchronously at startup, and a failed enqueue must surface there.
#pragma warning disable S4462 // Calls to "async" methods should not be blocking - StartMigration is void by contract and runs from the synchronous Module.PostInitialize
                backgroundJob.Enqueue<AssetReferenceIndexRebuildJob>(new AssetReferenceIndexRebuildJobPayload()).GetAwaiter().GetResult();
#pragma warning restore S4462
            }
        }
    }

    public async Task RebuildAssetReferenceIndex()
    {
        var pageMigrationCompleted = settingsManager.GetValue<bool>(Settings.Migration.AssetReferenceIndexMigrated);
        if (!pageMigrationCompleted)
        {
            await RebuildPageAssetReferenceIndex();
            await settingsManager.SetValueAsync(Settings.Migration.AssetReferenceIndexMigrated.Name, true);
        }
    }

    internal async Task RebuildPageAssetReferenceIndex()
    {
        PageCursor cursor = null;
        var pages = await GetPages(cursor);

        while (pages.Count > 0)
        {
            foreach (var pageId in pages.Select(page => page.Id))
            {
                await RebuildPageAssetReferenceIndexAsync(pageId);
            }

            cursor = pages[^1];
            pages = await GetPages(cursor);
        }
    }

    private async Task RebuildPageAssetReferenceIndexAsync(string pageId)
    {
        using var repository = repositoryFactory();
        await repository.RebuildPageAssetReferenceIndexAsync(pageId);
    }

    private async Task<IList<PageCursor>> GetPages(PageCursor cursor)
    {
        using var repository = repositoryFactory();

        var query = repository.SiteBuilderPages;
        if (cursor != null)
        {
            query = ApplyPageCursor(query, cursor.CreatedDate, cursor.Id);
        }

        return await query
            .OrderBy(x => x.CreatedDate)
            .ThenBy(x => x.Id)
            .Take(_batchSize)
            .Select(x => new PageCursor
            {
                Id = x.Id,
                CreatedDate = x.CreatedDate,
            })
            .ToListAsync();
    }

    internal static IQueryable<SiteBuilderPageEntity> ApplyPageCursor(
        IQueryable<SiteBuilderPageEntity> query,
        DateTime createdDate,
        string id)
    {
        return query.Where(x =>
            x.CreatedDate > createdDate ||
            x.CreatedDate == createdDate && string.Compare(x.Id, id) > 0);
    }

    private sealed class PageCursor
    {
        public string Id { get; init; }

        public DateTime CreatedDate { get; init; }
    }
}
