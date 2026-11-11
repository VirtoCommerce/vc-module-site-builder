using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Handlers;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.Pages.Core.Events;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class GroupedPageDeletedEventTests
{
    [Fact]
    public async Task Handle_DeletedGroup_RemovesItsPagesFromTheIndex()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"site-builder-deleted-group-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(TestContext.Current.CancellationToken);
        SiteBuilderDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<SiteBuilderDbContext>().UseSqlite(connectionString).Options);
        await using (var context = CreateContext())
        {
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        using var cache = new TestPlatformMemoryCache();
        var groups = new GroupedPageService(
            () => new SiteBuilderRepository(CreateContext()),
            () => throw new InvalidOperationException("Content is not read for a deleted group."),
            cache,
            new RecordingEventPublisher(),
            NullLogger<GroupedPageService>.Instance);
        var group = new GroupedSiteBuilderPage
        {
            StoreId = "store",
            Name = "Deleted",
            Permalink = "/deleted",
            CultureName = "en-US",
            Pages = [new SiteBuilderPage { StoreId = "store", Status = "Published" }],
        };
        await groups.SaveChangesAsync([group]);
        var deleted = await groups.GetByIdAsync(group.Id);
        await groups.DeleteAsync([group.Id]);

        var publisher = new RecordingEventPublisher();
        var handler = new GroupedSiteBuilderPageChangedEventHandler(
            publisher, groups, null!, new NoopAssetReferenceIndexService(), NullLogger<GroupedSiteBuilderPageChangedEventHandler>.Instance);

        await handler.Handle(new GroupedSiteBuilderPageChangedEvent(
            [new GenericChangedEntry<GroupedSiteBuilderPage>(deleted, EntryState.Deleted)]));

        var pagesEvent = Assert.Single(publisher.Events.OfType<PagesDomainEvent>());
        Assert.Equal(PageOperation.Delete, pagesEvent.Operation);
        Assert.Equal(deleted.Pages.Single().Id, pagesEvent.Page.Id);
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IEvent> Events { get; } = [];

        public Task Publish<T>(T @event, CancellationToken cancellationToken = default)
            where T : IEvent
        {
            lock (Events)
            {
                Events.Add(@event);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoopAssetReferenceIndexService : ISiteBuilderAssetReferenceIndexService
    {
        public Task DeletePageIndexAsync(IEnumerable<string> pageIds, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteGroupIndexAsync(IEnumerable<string> groupIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
