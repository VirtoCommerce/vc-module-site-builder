using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.Platform.Core.Domain;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderAssetReferenceMigrationServiceTests
{
    [Fact]
    public async Task RebuildPageAssetReferenceIndex_ConcurrentDeletionDoesNotSkipNextPage()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        var processedCount = 0;
        Func<ISiteBuilderRepository> repositoryFactory = () =>
            new CoordinatedRepository(
                database.CreateContext(),
                async () =>
                {
                    processedCount++;
                    if (processedCount == 50)
                    {
                        await database.DeletePageAsync("page-010");
                    }
                });
        var migration = new SiteBuilderAssetReferenceMigrationService(
            repositoryFactory,
            settingsManager: null,
            backgroundJob: null);

        await migration.RebuildPageAssetReferenceIndex();

        await using var verificationContext = database.CreateContext();
        var remainingPageIds = await verificationContext.Set<SiteBuilderPageEntity>()
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var indexedReferences = await verificationContext.Set<SiteBuilderAssetReferenceEntity>()
            .GroupBy(x => x.PageId)
            .Select(x => new
            {
                PageId = x.Key,
                Urls = x.Select(reference => reference.NormalizedAssetUrl).ToArray(),
            })
            .ToDictionaryAsync(x => x.PageId, x => x.Urls, TestContext.Current.CancellationToken);

        Assert.Equal(100, remainingPageIds.Length);
        Assert.Equal(101, processedCount);
        foreach (var pageId in remainingPageIds)
        {
            Assert.Equal([MigrationDatabase.CurrentAssetUrl], indexedReferences[pageId]);
        }
    }

    [Fact]
    public async Task RebuildPageAssetReferenceIndex_ConcurrentPageSaveCannotLeaveStaleSnapshotLast()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        var migrationHasPageLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMigration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseOnce = 0;
        Func<ISiteBuilderRepository> repositoryFactory = () =>
            new PausingRepository(
                database.CreateContext(),
                async pageIds =>
                {
                    if (pageIds.Contains("page-000") && Interlocked.CompareExchange(ref pauseOnce, 1, 0) == 0)
                    {
                        migrationHasPageLock.SetResult();
                        await releaseMigration.Task.WaitAsync(TestContext.Current.CancellationToken);
                    }
                });
        var migration = new SiteBuilderAssetReferenceMigrationService(
            repositoryFactory,
            settingsManager: null,
            backgroundJob: null);

        var migrationTask = migration.RebuildPageAssetReferenceIndex();
        await migrationHasPageLock.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pageSave = Task.Run(
            async () =>
            {
                saveStarted.SetResult();
                await database.SavePageAsync("page-000", MigrationDatabase.ReplacementAssetUrl);
            },
            TestContext.Current.CancellationToken);
        await saveStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.False(pageSave.IsCompleted);

        releaseMigration.SetResult();
        await Task.WhenAll(migrationTask, pageSave).WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        await using var context = database.CreateContext();
        var urls = await context.Set<SiteBuilderAssetReferenceEntity>()
            .Where(x => x.PageId == "page-000")
            .Select(x => x.NormalizedAssetUrl)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal([MigrationDatabase.ReplacementAssetUrl], urls);
    }

    [Fact]
    public void KeysetPredicates_TranslateForSqlServerProvider()
    {
        var options = new DbContextOptionsBuilder<SiteBuilderDbContext>()
            .UseSqlServer("Server=localhost;Database=translation-only;TrustServerCertificate=True")
            .Options;

        AssertKeysetPredicatesTranslate(options);
    }

    [Fact]
    public void KeysetPredicates_TranslateForPostgreSqlProvider()
    {
        var options = new DbContextOptionsBuilder<SiteBuilderDbContext>()
            .UseNpgsql("Host=localhost;Database=translation-only;Username=test;Password=test")
            .Options;

        AssertKeysetPredicatesTranslate(options);
    }

    [Fact]
    public void KeysetPredicates_TranslateForMySqlProvider()
    {
        var options = new DbContextOptionsBuilder<SiteBuilderDbContext>()
            .UseMySql(
                "Server=localhost;Database=translation-only;User=test;Password=test",
                new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;

        AssertKeysetPredicatesTranslate(options);
    }

    private static void AssertKeysetPredicatesTranslate(
        DbContextOptions<SiteBuilderDbContext> options)
    {
        using var context = new SiteBuilderDbContext(options);

        var pageSql = SiteBuilderAssetReferenceMigrationService.ApplyPageCursor(
                context.Set<SiteBuilderPageEntity>(),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                "page-050")
            .ToQueryString();
        Assert.Contains("WHERE", pageSql, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CoordinatedRepository(
        SiteBuilderDbContext dbContext,
        Func<Task> afterPage) : SiteBuilderRepository(dbContext)
    {
        public override async Task ExecuteUnderPageWriteLocksAsync(
            IEnumerable<string> pageIds,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            await base.ExecuteUnderPageWriteLocksAsync(pageIds, operation, cancellationToken);
            await afterPage();
        }
    }

    private sealed class PausingRepository(
        SiteBuilderDbContext dbContext,
        Func<IEnumerable<string>, Task> afterPageLocks) : SiteBuilderRepository(dbContext)
    {
        public override Task ExecuteUnderPageWriteLocksAsync(
            IEnumerable<string> pageIds,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            var ids = pageIds.ToArray();
            return base.ExecuteUnderPageWriteLocksAsync(
                ids,
                async transactionCancellationToken =>
                {
                    await afterPageLocks(ids);
                    await operation(transactionCancellationToken);
                },
                cancellationToken);
        }
    }

    private sealed class MigrationDatabase : IAsyncDisposable
    {
        public const string CurrentAssetUrl = "/stores/store/current.png";
        public const string ReplacementAssetUrl = "/stores/store/replacement.png";
        private const string StaleAssetUrl = "/stores/store/stale.png";
        private readonly SqliteConnection _anchorConnection;

        private MigrationDatabase(string connectionString, SqliteConnection anchorConnection)
        {
            ConnectionString = connectionString;
            _anchorConnection = anchorConnection;
        }

        private string ConnectionString { get; }

        public static async Task<MigrationDatabase> CreateAsync()
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"site-builder-migration-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
            }.ToString();
            var anchorConnection = new SqliteConnection(connectionString);
            await anchorConnection.OpenAsync(TestContext.Current.CancellationToken);
            var database = new MigrationDatabase(connectionString, anchorConnection);

            await using var context = database.CreateContext();
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var createdDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            context.Add(new GroupedSiteBuilderPageEntity
            {
                Id = "group",
                StoreId = "store",
                CreatedDate = createdDate,
            });

            for (var index = 0; index < 101; index++)
            {
                var pageId = $"page-{index:D3}";
                context.Add(new SiteBuilderPageEntity
                {
                    Id = pageId,
                    GroupId = "group",
                    StoreId = "store",
                    Status = "Published",
                    CreatedDate = createdDate,
                    Content = new SiteBuilderContentEntity
                    {
                        Id = pageId,
                        PageContent = $"{{ \"settings\": {{ \"image\": \"{CurrentAssetUrl}\" }}, \"content\": [] }}",
                    },
                });
                context.Add(new SiteBuilderAssetReferenceEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    PageId = pageId,
                    NormalizedAssetUrl = StaleAssetUrl,
                    NormalizedAssetUrlHash = SiteBuilderAssetReferenceMatcher.GetAssetUrlHash(StaleAssetUrl),
                });

            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public SiteBuilderDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<SiteBuilderDbContext>()
                .UseSqlite(ConnectionString)
                .Options;
            return new SiteBuilderDbContext(options);
        }

        public async Task DeletePageAsync(string pageId)
        {
            await using var context = CreateContext();
            var page = await context.Set<SiteBuilderPageEntity>()
                .SingleAsync(x => x.Id == pageId, TestContext.Current.CancellationToken);
            context.Remove(page);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task SavePageAsync(string pageId, string assetUrl)
        {
            await using var context = CreateContext();
            var repository = new SqliteContentStreamRepository(context);
            var content = $"{{ \"settings\": {{ \"image\": \"{assetUrl}\" }}, \"content\": [] }}";
            await repository.SavePageContentAsync(
                pageId,
                content,
                TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _anchorConnection.DisposeAsync();
        }
    }

    private sealed class SqliteContentStreamRepository(SiteBuilderDbContext dbContext)
        : ContentStreamRepository(dbContext)
    {
        protected override string QuoteOpen => "\"";

        protected override string QuoteClose => "\"";

        protected override string AppendContentChunkSql =>
            $"UPDATE {Table} SET {ContentColumn} = {ContentColumn} || @chunk WHERE {IdColumn} = @id";

        protected override void SetIdParameter(DbCommand cmd, string value)
        {
            cmd.Parameters.Add(new SqliteParameter("@id", value));
        }

        protected override void SetContentChunk(DbCommand cmd, string chunk)
        {
            cmd.Parameters.Add(new SqliteParameter("@chunk", chunk));
        }
    }
}
