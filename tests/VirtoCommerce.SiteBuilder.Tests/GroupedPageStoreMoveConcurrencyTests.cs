using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.Pages.Core.Events;
using VirtoCommerce.Platform.Caching;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Events;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class GroupedPageStoreMoveConcurrencyTests
{
    private const string ComponentId = "component";
    private const string GroupId = "group";
    private const string PageId = "page";

    [Fact]
    public async Task SaveChangesAsync_ReferenceAppearingInsideWriteLockRejectsMoveAndRollsBack()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedAsync();
        using var cache = new TestPlatformMemoryCache();
        var service = new GroupedPageService(
            () => new CoordinatedRepository(database.CreateContext()),
            () => throw new InvalidOperationException("Content repository is not used by this test."),
            cache,
            new NoopEventPublisher(),
            NullLogger<GroupedPageService>.Instance);
        var model = new GroupedSiteBuilderPage
        {
            Id = GroupId,
            StoreId = "store-b",
            Pages =
            [
                new SiteBuilderPage
                {
                    Id = PageId,
                    GroupId = GroupId,
                    StoreId = "store-a",
                    Status = "Draft",
                },
            ],
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveChangesAsync([model]));

        await using var verificationContext = database.CreateContext();
        Assert.Equal(
            "store-a",
            await verificationContext.Set<GroupedSiteBuilderPageEntity>()
                .Where(x => x.Id == GroupId)
                .Select(x => x.StoreId)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "store-a",
            await verificationContext.Set<SiteBuilderPageEntity>()
                .Where(x => x.Id == PageId)
                .Select(x => x.StoreId)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verificationContext.Set<SiteBuilderSharedComponentReferenceEntity>()
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveChangesAsync_LegacyGroupMoveCommitsGroupAndPageStoreTogether()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedAsync();
        using var cache = new TestPlatformMemoryCache();
        var service = new GroupedPageService(
            () => new SiteBuilderRepository(database.CreateContext()),
            () => throw new InvalidOperationException("Content repository is not used by this test."),
            cache,
            new NoopEventPublisher(),
            NullLogger<GroupedPageService>.Instance);
        var model = new GroupedSiteBuilderPage
        {
            Id = GroupId,
            StoreId = "store-b",
            Pages =
            [
                new SiteBuilderPage
                {
                    Id = PageId,
                    GroupId = GroupId,
                    StoreId = "store-a",
                    Status = "Draft",
                },
            ],
        };

        await service.SaveChangesAsync([model]);

        await using var verificationContext = database.CreateContext();
        Assert.Equal(
            "store-b",
            await verificationContext.Set<GroupedSiteBuilderPageEntity>()
                .Where(x => x.Id == GroupId)
                .Select(x => x.StoreId)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "store-b",
            await verificationContext.Set<SiteBuilderPageEntity>()
                .Where(x => x.Id == PageId)
                .Select(x => x.StoreId)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryDeleteEmptyDraftAsync_DeletesOnlyStillEmptyDraft()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedAsync();
        using var cache = new TestPlatformMemoryCache();
        var events = new CancellationAwareEventPublisher();
        var service = CreateService(database, cache, events);
        using var cancellationTokenSource = new CancellationTokenSource();
        var groupedSearchCacheKey = $"grouped-search-{Guid.NewGuid():N}";
        using (var cacheEntry = cache.CreateEntry(groupedSearchCacheKey))
        {
            cacheEntry.Value = true;
            cacheEntry.ExpirationTokens.Add(
                GenericSearchCachingRegion<GroupedSiteBuilderPage>.CreateChangeToken());
        }
        Assert.True(cache.TryGetValue(groupedSearchCacheKey, out _));

        var deleted = await service.TryDeleteEmptyDraftAsync(
            PageId,
            cancellationTokenSource.Token);

        Assert.True(deleted);
        Assert.True(cancellationTokenSource.Token.CanBeCanceled);
        Assert.False(cache.TryGetValue(groupedSearchCacheKey, out _));
        var pagesEvent = Assert.Single(events.Events.OfType<PagesDomainEvent>());
        Assert.Equal(PageId, pagesEvent.Id);
        Assert.Equal(PageId, pagesEvent.Page.Id);
        Assert.Equal(PageOperation.Delete, pagesEvent.Operation);
        Assert.False(Assert.Single(events.CancellationTokens).CanBeCanceled);
        await using var verificationContext = database.CreateContext();
        Assert.False(await verificationContext.Set<SiteBuilderPageEntity>()
            .AnyAsync(x => x.Id == PageId, TestContext.Current.CancellationToken));
        Assert.True(await verificationContext.Set<GroupedSiteBuilderPageEntity>()
            .AnyAsync(x => x.Id == GroupId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryDeleteEmptyDraftAsync_PreservesDraftWithCommittedContent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedAsync();
        await database.SetPageContentAsync("{ \"concurrent\": true }");
        using var cache = new TestPlatformMemoryCache();
        var events = new CancellationAwareEventPublisher();
        var service = CreateService(database, cache, events);

        var deleted = await service.TryDeleteEmptyDraftAsync(
            PageId,
            TestContext.Current.CancellationToken);

        Assert.False(deleted);
        Assert.Empty(events.Events);
        await using var verificationContext = database.CreateContext();
        Assert.True(await verificationContext.Set<SiteBuilderPageEntity>()
            .AnyAsync(x => x.Id == PageId, TestContext.Current.CancellationToken));
    }

    private static GroupedPageService CreateService(
        TestDatabase database,
        IPlatformMemoryCache cache,
        IEventPublisher eventPublisher = null)
    {
        return new GroupedPageService(
            () => new SiteBuilderRepository(database.CreateContext()),
            () => throw new InvalidOperationException("Content repository is not used by this test."),
            cache,
            eventPublisher ?? new NoopEventPublisher(),
            NullLogger<GroupedPageService>.Instance);
    }

    private sealed class CoordinatedRepository(SiteBuilderDbContext dbContext)
        : SiteBuilderRepository(dbContext)
    {
        public override Task ExecuteUnderGroupedPageWriteLocksAsync(
            IEnumerable<string> groupIds,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            return base.ExecuteUnderGroupedPageWriteLocksAsync(
                groupIds,
                async transactionCancellationToken =>
                {
                    Add(new SiteBuilderSharedComponentReferenceEntity
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        PageId = PageId,
                        SharedComponentId = ComponentId,
                    });
                    await UnitOfWork.CommitAsync();
                    await operation(transactionCancellationToken);
                },
                cancellationToken);
        }
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task Publish<T>(T @event, CancellationToken cancellationToken = default)
            where T : IEvent => Task.CompletedTask;
    }

    private sealed class CancellationAwareEventPublisher : IEventPublisher
    {
        public IList<IEvent> Events { get; } = [];
        public IList<CancellationToken> CancellationTokens { get; } = [];

        public Task Publish<T>(T @event, CancellationToken cancellationToken = default)
            where T : IEvent
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(@event);
            CancellationTokens.Add(cancellationToken);
            return Task.CompletedTask;
        }
    }



    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _anchorConnection;

        private TestDatabase(string connectionString, SqliteConnection anchorConnection)
        {
            ConnectionString = connectionString;
            _anchorConnection = anchorConnection;
        }

        private string ConnectionString { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"site-builder-store-move-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 10,
            }.ToString();
            var anchorConnection = new SqliteConnection(connectionString);
            await anchorConnection.OpenAsync(TestContext.Current.CancellationToken);
            var database = new TestDatabase(connectionString, anchorConnection);

            await using var context = database.CreateContext();
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public SiteBuilderDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<SiteBuilderDbContext>()
                .UseSqlite(ConnectionString)
                .Options;
            return new SiteBuilderDbContext(options);
        }

        public async Task SeedAsync()
        {
            await using var context = CreateContext();
            var now = DateTime.UtcNow;
            context.Add(new GroupedSiteBuilderPageEntity
            {
                Id = GroupId,
                StoreId = "store-a",
                CreatedDate = now,
                Pages = new ObservableCollection<SiteBuilderPageEntity>
                {
                    new()
                    {
                        Id = PageId,
                        GroupId = GroupId,
                        StoreId = "store-a",
                        Status = "Draft",
                        CreatedDate = now,
                    },
                },
            });
            context.Add(new SiteBuilderSharedComponentEntity
            {
                Id = ComponentId,
                StoreId = "store-a",
                Name = "Shared component",
                CreatedDate = now,
                Content = new SiteBuilderSharedComponentContentEntity
                {
                    Id = ComponentId,
                    ComponentContent = "{}",
                },
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task SetPageContentAsync(string content)
        {
            await using var context = CreateContext();
            var pageContent = new SiteBuilderContentEntity
            {
                Id = PageId,
                PageContent = content,
            };
            context.Attach(pageContent);
            context.Entry(pageContent).Property(x => x.PageContent).IsModified = true;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _anchorConnection.DisposeAsync();
        }
    }
}
