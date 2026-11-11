using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Platform.Data.Model;
using VirtoCommerce.Platform.Data.Repositories;
using VirtoCommerce.Platform.Data.Settings;
using VirtoCommerce.SiteBuilder.Data.BackgroundJobs;
using VirtoCommerce.SiteBuilder.Data.GitContent;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;
using Xunit;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Settings;

namespace VirtoCommerce.SiteBuilder.Tests;

public class PageBuilderSettingsMigrationServiceTests
{
    private const string Store = "Store";
    private const string AppConnectionJson = "{\"appId\":\"42\",\"privateKey\":\"secret-key\"}";

    [Fact]
    public async Task CopySettingsAsync_SiteBuilderHasNoValue_CopiesPageBuilderValues()
    {
        await using var setup = await Setup.CreateAsync("s1", "s2");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.General.StoreUrl", SettingValueType.ShortText, "https://shop.example");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.General.OzAgentUrl", SettingValueType.ShortText, "https://oz.example");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.NewPageBuilder", SettingValueType.Boolean, true, Store, "s1");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.GitContentEnabled", SettingValueType.Boolean, true, Store, "s2");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.GitContentRepository", SettingValueType.ShortText, "acme/site-s2", Store, "s2");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.PreviewUserIds", SettingValueType.LongText, "u1,u2", Store, "s2");

        await setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://shop.example", await setup.GetAsync<string>(General.StoreUrl));
        Assert.Equal("https://oz.example", await setup.GetAsync<string>(General.OzAgentUrl));
        Assert.Equal("/designer-preview", await setup.GetAsync<string>(General.StorePreviewPath));
        Assert.True(await setup.GetAsync<bool>(StoreLevelSettings.NewSiteBuilder, "s1"));
        Assert.False(await setup.GetAsync<bool>(StoreLevelSettings.NewSiteBuilder, "s2"));
        Assert.True(await setup.GetAsync<bool>(StoreLevelSettings.GitContentEnabled, "s2"));
        Assert.Equal("acme/site-s2", await setup.GetAsync<string>(StoreLevelSettings.GitContentRepository, "s2"));
        Assert.Equal("u1,u2", await setup.GetAsync<string>(StoreLevelSettings.PreviewUserIds, "s2"));
        Assert.True(await setup.GetAsync<bool>(Migration.SettingsCopiedFromPageBuilder));
        Assert.False(await setup.HasRowAsync(StoreLevelSettings.NewSiteBuilder.Name, Store, "s2"));
    }

    [Fact]
    public async Task CopySettingsAsync_SiteBuilderHasStoredValue_KeepsIt()
    {
        await using var setup = await Setup.CreateAsync("s1");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.General.StoreUrl", SettingValueType.ShortText, "https://old.example");
        await setup.AddRowAsync(General.StoreUrl.Name, SettingValueType.ShortText, "https://new.example");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.NewPageBuilder", SettingValueType.Boolean, true, Store, "s1");
        // stored as its default: still a value the admin chose
        await setup.AddRowAsync(StoreLevelSettings.NewSiteBuilder.Name, SettingValueType.Boolean, false, Store, "s1");

        await setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://new.example", await setup.GetAsync<string>(General.StoreUrl));
        Assert.False(await setup.GetAsync<bool>(StoreLevelSettings.NewSiteBuilder, "s1"));
    }

    [Fact]
    public async Task CopySettingsAsync_ValueReadBeforeTheCopy_IsNotServedFromCache()
    {
        await using var setup = await Setup.CreateAsync("s1");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.NewPageBuilder", SettingValueType.Boolean, true, Store, "s1");
        Assert.False(await setup.GetAsync<bool>(StoreLevelSettings.NewSiteBuilder, "s1"));

        await setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken);

        Assert.True(await setup.GetAsync<bool>(StoreLevelSettings.NewSiteBuilder, "s1"));
    }

    [Fact]
    public async Task CopySettingsAsync_FlagSet_CopiesNothing()
    {
        await using var setup = await Setup.CreateAsync("s1");
        await setup.AddRowAsync(Migration.SettingsCopiedFromPageBuilder.Name, SettingValueType.Boolean, true);
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.NewPageBuilder", SettingValueType.Boolean, true, Store, "s1");

        await setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken);

        Assert.False(await setup.HasRowAsync(StoreLevelSettings.NewSiteBuilder.Name, Store, "s1"));
        Assert.Empty(setup.Stores.Requests);
    }

    [Fact]
    public async Task CopySettingsAsync_SecondRun_CopiesNothingAndLogsOnce()
    {
        await using var setup = await Setup.CreateAsync("s1");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.NewPageBuilder", SettingValueType.Boolean, true, Store, "s1");
        await setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken);
        await setup.SetAsync(StoreLevelSettings.NewSiteBuilder, false, "s1");

        await setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken);

        Assert.False(await setup.GetAsync<bool>(StoreLevelSettings.NewSiteBuilder, "s1"));
        var information = Assert.Single(setup.Logger.Entries, x => x.Level == LogLevel.Information);
        Assert.Contains($"{StoreLevelSettings.NewSiteBuilder.Name}=1", information.Message);
        Assert.Contains($"{General.StoreUrl.Name}=0", information.Message);
        Assert.DoesNotContain("True", information.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopySettingsAsync_FailsPartWay_LeavesFlagUnsetAndRetryKeepsSiteBuilderValues()
    {
        await using var setup = await Setup.CreateAsync(Enumerable.Range(0, 60).Select(x => $"s{x:D2}").ToArray());
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.General.StoreUrl", SettingValueType.ShortText, "https://old.example");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.NewPageBuilder", SettingValueType.Boolean, true, Store, "s00");
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.NewPageBuilder", SettingValueType.Boolean, true, Store, "s59");
        setup.Stores.FailAtSkip = PageBuilderSettingsMigrationService.StorePageSize;

        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken));

        Assert.False(await setup.GetAsync<bool>(Migration.SettingsCopiedFromPageBuilder));
        Assert.True(await setup.GetAsync<bool>(StoreLevelSettings.NewSiteBuilder, "s00"));
        Assert.False(await setup.HasRowAsync(StoreLevelSettings.NewSiteBuilder.Name, Store, "s59"));

        // the admin changes Site Builder's values before the next start
        await setup.SetAsync(General.StoreUrl, "https://new.example");
        await setup.SetAsync(StoreLevelSettings.NewSiteBuilder, false, "s00");
        setup.Stores.FailAtSkip = null;

        await setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken);

        Assert.True(await setup.GetAsync<bool>(Migration.SettingsCopiedFromPageBuilder));
        Assert.Equal("https://new.example", await setup.GetAsync<string>(General.StoreUrl));
        Assert.False(await setup.GetAsync<bool>(StoreLevelSettings.NewSiteBuilder, "s00"));
        Assert.True(await setup.GetAsync<bool>(StoreLevelSettings.NewSiteBuilder, "s59"));
    }

    [Fact]
    public async Task CopySettingsAsync_ManyStores_ReadsThemPageByPage()
    {
        var storeIds = Enumerable.Range(0, 120).Select(x => $"s{x:D3}").ToArray();
        await using var setup = await Setup.CreateAsync(storeIds);
        foreach (var storeId in storeIds)
        {
            await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.Store.NewPageBuilder", SettingValueType.Boolean, true, Store, storeId);
        }

        await setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal([0, 50, 100], setup.Stores.Requests.Select(x => x.Skip));
        Assert.All(setup.Stores.Requests, x => Assert.Equal(PageBuilderSettingsMigrationService.StorePageSize, x.Take));
        foreach (var storeId in storeIds)
        {
            Assert.True(await setup.GetAsync<bool>(StoreLevelSettings.NewSiteBuilder, storeId));
        }
    }

    [Fact]
    public async Task CopySettingsAsync_AppConnection_IsProtectedForSiteBuilder()
    {
        await using var setup = await Setup.CreateAsync();
        var protectedByPageBuilder = setup.Keys.CreateProtector(PageBuilderSettingsMigrationService.PageBuilderAppConnectionPurpose).Protect(AppConnectionJson);
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.GitHub.AppConnection", SettingValueType.LongText, protectedByPageBuilder);

        await setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken);

        var stored = await setup.GetAsync<string>(GitHub.AppConnection);
        Assert.NotEqual(protectedByPageBuilder, stored);
        Assert.Equal(AppConnectionJson, setup.Keys.CreateProtector(GitHubConnectionService.ProtectionPurpose).Unprotect(stored));
    }

    [Fact]
    public async Task CopySettingsAsync_AppConnectionCannotBeDecrypted_SkipsItWithWarning()
    {
        await using var setup = await Setup.CreateAsync();
        var protectedElsewhere = new EphemeralDataProtectionProvider()
            .CreateProtector(PageBuilderSettingsMigrationService.PageBuilderAppConnectionPurpose)
            .Protect(AppConnectionJson);
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.GitHub.AppConnection", SettingValueType.LongText, protectedElsewhere);
        await setup.AddRowAsync("VirtoCommerce.PageBuilderModule.General.StoreUrl", SettingValueType.ShortText, "https://shop.example");

        await setup.Service.CopySettingsAsync(TestContext.Current.CancellationToken);

        Assert.False(await setup.HasRowAsync(GitHub.AppConnection.Name, null, null));
        Assert.Equal("https://shop.example", await setup.GetAsync<string>(General.StoreUrl));
        Assert.True(await setup.GetAsync<bool>(Migration.SettingsCopiedFromPageBuilder));
        var warning = Assert.Single(setup.Logger.Entries, x => x.Level == LogLevel.Warning);
        Assert.Contains("VirtoCommerce.PageBuilderModule.GitHub.AppConnection", warning.Message);
        Assert.DoesNotContain(setup.Logger.Entries, x => x.Message.Contains(protectedElsewhere) || x.Message.Contains("secret-key"));
    }

    [Fact]
    public async Task StartMigration_EnqueuesTheCopyUntilTheFlagIsSet()
    {
        await using var setup = await Setup.CreateAsync();

        setup.Service.StartMigration();
        await setup.SetAsync(Migration.SettingsCopiedFromPageBuilder, true);
        setup.Service.StartMigration();

        Assert.Equal([typeof(PageBuilderSettingsCopyJob)], setup.Jobs.Enqueued);
    }

    private sealed class Setup : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<PlatformDbContext> _options;

        private Setup(SqliteConnection connection, string[] storeIds)
        {
            _connection = connection;
            _options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;

            var settingsManager = new SettingsManager(
                () => new PlatformRepository(new PlatformDbContext(_options)),
                new TestPlatformMemoryCache(),
                Mock.Of<IEventPublisher>(),
                Options.Create(new FixedSettings()),
                overrideProvider: null);
            settingsManager.RegisterSettings(AllSettings, "VirtoCommerce.SiteBuilder");
            settingsManager.RegisterSettingsForType(StoreLevelSettings.AllStoreLevelSettings, StoreSettingsObjectType);
            SettingsManager = settingsManager;

            Stores = new PagedStoreSearchService(storeIds);
            Service = new PageBuilderSettingsMigrationService(
                () => new PlatformRepository(new PlatformDbContext(_options)),
                settingsManager,
                Stores,
                Keys,
                Jobs,
                Logger);
        }

        public ISettingsManager SettingsManager { get; }
        public PagedStoreSearchService Stores { get; }
        public IDataProtectionProvider Keys { get; } = new EphemeralDataProtectionProvider();
        public RecordingBackgroundJob Jobs { get; } = new();
        public RecordingLogger Logger { get; } = new();
        public PageBuilderSettingsMigrationService Service { get; }

        public static async Task<Setup> CreateAsync(params string[] storeIds)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var setup = new Setup(connection, storeIds);
            await using var context = new PlatformDbContext(setup._options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return setup;
        }

        public async Task AddRowAsync(string name, SettingValueType valueType, object value, string objectType = null, string objectId = null)
        {
            await using var context = new PlatformDbContext(_options);
            var row = new SettingEntity { Name = name, ObjectType = objectType, ObjectId = objectId };
            row.SettingValues = [new SettingValueEntity().SetValue(valueType, value)];
            context.Add(row);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task<bool> HasRowAsync(string name, string objectType, string objectId)
        {
            await using var context = new PlatformDbContext(_options);
            return await context.Set<SettingEntity>().AnyAsync(x => x.Name == name && x.ObjectType == objectType && x.ObjectId == objectId, TestContext.Current.CancellationToken);
        }

        public async Task<T> GetAsync<T>(SettingDescriptor descriptor, string storeId = null)
        {
            var objectType = storeId == null ? null : StoreSettingsObjectType;
            var setting = await SettingsManager.GetObjectSettingAsync(descriptor.Name, objectType, storeId);
            return (T)(setting.Value ?? setting.DefaultValue);
        }

        public async Task SetAsync(SettingDescriptor descriptor, object value, string storeId = null)
        {
            var objectType = storeId == null ? null : StoreSettingsObjectType;
            var setting = await SettingsManager.GetObjectSettingAsync(descriptor.Name, objectType, storeId);
            setting.Value = value;
            await SettingsManager.SaveObjectSettingsAsync([setting]);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class PagedStoreSearchService(string[] storeIds) : IStoreSearchService
    {
        public List<StoreSearchCriteria> Requests { get; } = [];

        public int? FailAtSkip { get; set; }

        public Task<StoreSearchResult> SearchAsync(StoreSearchCriteria criteria, bool clone = true)
        {
            Requests.Add(new StoreSearchCriteria { Skip = criteria.Skip, Take = criteria.Take, Sort = criteria.Sort });
            if (criteria.Skip == FailAtSkip)
            {
                throw new InvalidOperationException("The store search does not answer.");
            }

            return Task.FromResult(new StoreSearchResult
            {
                TotalCount = storeIds.Length,
                Results = storeIds.Order(StringComparer.Ordinal).Skip(criteria.Skip).Take(criteria.Take).Select(id => new Store { Id = id }).ToList(),
            });
        }
    }

    private sealed class RecordingBackgroundJob : IBackgroundJob
    {
        public List<Type> Enqueued { get; } = [];

        public Task<string> Enqueue<THandler>(object payload, EnqueueOptions options = null, CancellationToken cancellationToken = default)
            where THandler : class
        {
            Enqueued.Add(typeof(THandler));
            return Task.FromResult("job-id");
        }
    }

    private sealed class RecordingLogger : ILogger<PageBuilderSettingsMigrationService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception) + exception?.Message));
        }
    }
}
