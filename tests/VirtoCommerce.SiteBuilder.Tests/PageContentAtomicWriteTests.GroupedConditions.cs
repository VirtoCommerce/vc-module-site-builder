using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public partial class PageContentAtomicWriteTests
{
    [Fact]
    public async Task DeleteDraftsAtVersion_TheReviewedDraft_IsDeletedWithItsEvents_AndThePublishedPageStays()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SaveRawAsync(database.ConnectionString, PageId, ComponentAContent);
        var group = await LoadGroupAsync(database.ConnectionString);
        var events = new RecordingEventPublisher();

        var deleted = await CreateService(database.ConnectionString, events)
            .DeleteDraftsAtVersionAsync(group, SiteBuilderContentVersion.Create(group, ComponentAContent), TestContext.Current.CancellationToken);

        Assert.Equal([PageId], deleted);
        Assert.Equal([SourcePageId], await LoadPageIdsAsync(database.ConnectionString));
        Assert.Equal([EntryState.Deleted], events.Events.OfType<SiteBuilderPageChangingEvent>().SelectMany(x => x.ChangedEntries).Select(x => x.EntryState));
        Assert.Equal([PageId], events.Events.OfType<SiteBuilderPageChangedEvent>().SelectMany(x => x.ChangedEntries).Select(x => x.NewEntry.Id));
    }

    [Fact]
    public async Task DeleteDraftsAtVersion_DraftSavedSinceTheReview_IsAConflictAndDeletesNothing()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SaveRawAsync(database.ConnectionString, PageId, ComponentAContent);
        var group = await LoadGroupAsync(database.ConnectionString);
        var reviewed = SiteBuilderContentVersion.Create(group, ComponentAContent);
        await SaveConditionalAsync(database.ConnectionString, group, ComponentBContent, reviewed);

        await Assert.ThrowsAsync<SiteBuilderContentConflictException>(() =>
            CreateService(database.ConnectionString).DeleteDraftsAtVersionAsync(group, reviewed, TestContext.Current.CancellationToken));

        Assert.Equal([PageId, SourcePageId], await LoadPageIdsAsync(database.ConnectionString));
        Assert.Equal(ComponentBContent, await LoadContentAsync(database.ConnectionString));
    }

    // SQLite serializes whole transactions: the discard cannot read the version until the save holding the lock has committed.
    [Fact]
    public async Task DeleteDraftsAtVersion_ASaveHoldingTheLock_IsWaitedFor_AndTheDraftItWroteIsKept()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SaveRawAsync(database.ConnectionString, PageId, ComponentAContent);
        var group = await LoadGroupAsync(database.ConnectionString);
        var reviewed = SiteBuilderContentVersion.Create(group, ComponentAContent);
        var atCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = SaveConditionalAsync(database.ConnectionString, group, ComponentBContent, reviewed, async ct =>
        {
            atCommit.SetResult();
            await release.Task.WaitAsync(ct);
        });
        await atCommit.Task.WaitAsync(TestContext.Current.CancellationToken);

        var discard = Task.Run(() => CreateService(database.ConnectionString).DeleteDraftsAtVersionAsync(group, reviewed, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(discard.IsCompleted);
        release.SetResult();
        await save;

        await Assert.ThrowsAsync<SiteBuilderContentConflictException>(() => discard);
        Assert.Equal(ComponentBContent, await LoadContentAsync(database.ConnectionString));
    }

    [Fact]
    public async Task DeleteDraftsAtVersion_NoDraftLeft_IsAConflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SaveRawAsync(database.ConnectionString, PageId, ComponentAContent);
        var group = await LoadGroupAsync(database.ConnectionString);
        var reviewed = SiteBuilderContentVersion.Create(group, ComponentAContent);
        await CreateService(database.ConnectionString).DeleteDraftsAtVersionAsync(group, reviewed, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SiteBuilderContentConflictException>(() =>
            CreateService(database.ConnectionString).DeleteDraftsAtVersionAsync(group, reviewed, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveGroupSettings_AtTheReviewedSettingsVersion_Saves()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = CreateService(database.ConnectionString);
        var group = await service.GetByIdAsync(GroupId);
        var condition = new GroupedPageSaveCondition { ExpectedSettingsVersion = SiteBuilderContentVersion.CreateForGroupSettings(group) };
        group.Visibility = true;

        await service.SaveGroupSettingsAsync(group, condition, TestContext.Current.CancellationToken);

        Assert.True((await LoadGroupEntityAsync(database.ConnectionString)).Visibility);
    }

    [Fact]
    public async Task SaveGroupSettings_SettingsChangedSinceTheReview_IsAConflictAndKeepsTheOtherChange()
    {
        await using var database = await TestDatabase.CreateAsync();
        var group = await CreateService(database.ConnectionString).GetByIdAsync(GroupId);
        var condition = new GroupedPageSaveCondition { ExpectedSettingsVersion = SiteBuilderContentVersion.CreateForGroupSettings(group) };
        var colleagues = await CreateService(database.ConnectionString).GetByIdAsync(GroupId);
        colleagues.UserGroups = "staff";
        await CreateService(database.ConnectionString).SaveGroupSettingsAsync(colleagues, TestContext.Current.CancellationToken);
        group.Visibility = true;

        var conflict = await Assert.ThrowsAsync<SiteBuilderContentConflictException>(() =>
            CreateService(database.ConnectionString).SaveGroupSettingsAsync(group, condition, TestContext.Current.CancellationToken));

        Assert.Equal(SiteBuilderContentConflictException.SettingsChangedMessage, conflict.Message);
        var stored = await LoadGroupEntityAsync(database.ConnectionString);
        Assert.Equal(("staff", false), (stored.UserGroups, stored.Visibility));
    }

    [Fact]
    public async Task SaveGroupSettings_RequireUnpublished_OfAPublishedPage_IsAConflictAndWritesNothing()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = CreateService(database.ConnectionString);
        var group = await service.GetByIdAsync(GroupId);
        group.Permalink = "/elsewhere";

        var conflict = await Assert.ThrowsAsync<SiteBuilderContentConflictException>(() =>
            service.SaveGroupSettingsAsync(group, new GroupedPageSaveCondition { RequireUnpublished = true }, TestContext.Current.CancellationToken));

        Assert.Equal(SiteBuilderContentConflictException.PublishedPageMessage, conflict.Message);
        Assert.Null((await LoadGroupEntityAsync(database.ConnectionString)).Permalink);
    }

    [Fact]
    public async Task SaveGroupSettings_RequireUnpublished_OfAPageWithNoPublishedVersion_Saves()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var context = CreateContext(database.ConnectionString))
        {
            var source = await context.Set<SiteBuilderPageEntity>().SingleAsync(x => x.Id == SourcePageId, TestContext.Current.CancellationToken);
            source.Status = "Archived";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var service = CreateService(database.ConnectionString);
        var group = await service.GetByIdAsync(GroupId);
        group.Permalink = "/elsewhere";

        await service.SaveGroupSettingsAsync(group, new GroupedPageSaveCondition { RequireUnpublished = true }, TestContext.Current.CancellationToken);

        Assert.Equal("/elsewhere", (await LoadGroupEntityAsync(database.ConnectionString)).Permalink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveGroup_SourceRevisionCondition_IsReadFromTheSourceUnderTheLock(bool republished)
    {
        await using var database = await TestDatabase.CreateAsync();
        var revision = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        await using (var context = CreateContext(database.ConnectionString))
        {
            context.Add(new GroupedSiteBuilderPageEntity { Id = "source", StoreId = StoreId, CreatedDate = DateTime.UtcNow, SourceRevisionDate = revision });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        if (republished)
        {
            await using var context = CreateContext(database.ConnectionString);
            (await context.Set<GroupedSiteBuilderPageEntity>().SingleAsync(x => x.Id == "source", TestContext.Current.CancellationToken)).SourceRevisionDate =
                revision.AddHours(1);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var service = CreateService(database.ConnectionString);
        var group = await service.GetByIdAsync(GroupId);
        group.SourceRevisionDate = revision;
        var condition = new GroupedPageSaveCondition { SourceGroupId = "source", ExpectedSourceRevisionDate = revision };

        var save = service.SaveGroupAsync(group, condition, TestContext.Current.CancellationToken);

        if (republished)
        {
            await Assert.ThrowsAsync<SiteBuilderContentConflictException>(() => save);
            Assert.Null((await LoadGroupEntityAsync(database.ConnectionString)).SourceRevisionDate);
        }
        else
        {
            await save;
            Assert.Equal(revision, (await LoadGroupEntityAsync(database.ConnectionString)).SourceRevisionDate);
        }
    }

    private static GroupedPageService CreateService(string connectionString, IEventPublisher events = null) =>
        new(() => new SiteBuilderRepository(CreateContext(connectionString)),
            () => new SqliteContentStreamRepository(CreateContext(connectionString)),
            new TestPlatformMemoryCache(), events ?? new NoopEventPublisher(), NullLogger<GroupedPageService>.Instance);

    private static async Task<string[]> LoadPageIdsAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        return await context.Set<SiteBuilderPageEntity>().Where(x => x.GroupId == GroupId).OrderBy(x => x.Id).Select(x => x.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<GroupedSiteBuilderPageEntity> LoadGroupEntityAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        return await context.Set<GroupedSiteBuilderPageEntity>().AsNoTracking().SingleAsync(x => x.Id == GroupId, TestContext.Current.CancellationToken);
    }
}
