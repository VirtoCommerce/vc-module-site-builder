using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Newtonsoft.Json;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.JsonConverters;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.SiteBuilder.MCP;
using VirtoCommerce.SiteBuilder.MCP.Jobs;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Security;
using Fakes = VirtoCommerce.SiteBuilder.Tests.PublishedRenameContentPreservationTests;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

// The Site Builder toolset served by the MCP test harness: the module's real store authorization (the handler and its
// requirement) and history request validation, over services the test fills in.
internal sealed class McpTestContext : IAsyncDisposable
{
    public const string StoreA = "store-a";
    public const string StoreB = "store-b";
    public const string StoreC = "store-c";
    public const string StoreScope = nameof(StoreSelectedScope);
    public const string AssetRead = "platform:asset:read";

    // The store every user of the test is bound to, if any. The handler finds the user by the id claim, whose types
    // the platform sets at startup, so here every lookup answers the same account.
    private string _boundStoreId;
    private McpToolHarness _harness;

    // The claim types the platform reads the user id from, as its startup sets them: a bulk tool stores the id for its job.
    static McpTestContext()
    {
        ClaimsPrincipalExtensions.UserIdClaimTypes = ["sub", ClaimTypes.NameIdentifier];
    }

    public Mock<ISiteBuilderPageListService> PageList { get; } = new();
    public Mock<ISiteBuilderPageSearchService> PageVersionSearch { get; } = new();
    public Mock<IGroupedPageService> GroupedPages { get; } = new();
    public Mock<IGroupedPageContentService> PageContents { get; } = new();
    public Mock<ISiteBuilderAssetSearchService> AssetSearch { get; } = new();
    public Mock<ISiteBuilderAssetReferenceService> AssetReferences { get; } = new();
    public Mock<ISiteBuilderSharedComponentSearchService> SharedComponentSearch { get; } = new();
    public Mock<ISiteBuilderSharedComponentService> SharedComponents { get; } = new();
    public Mock<ISiteBuilderSharedComponentContentService> SharedComponentContents { get; } = new();
    public Mock<ISiteBuilderSharedComponentUsageService> SharedComponentUsages { get; } = new();
    public Mock<IContentHistory> History { get; } = new();
    public Mock<IMultilanguagePageService> MultilanguagePages { get; } = new();
    public Mock<IUserNameResolver> UserNames { get; } = new();
    public RecordingJobDispatcher Jobs { get; } = new();

    // The accounts a job runs as, by user id, as a sign-in would give them when the job runs; a missing one no longer exists.
    public Dictionary<string, ClaimsPrincipal> JobUsers { get; } = new(StringComparer.Ordinal);

    // Pages with their content, saved through the designer's own save; null unless a test calls UsePageStore.
    public Fakes.FakeGroupedPageService PageStore { get; private set; }

    public RecordingContentHistory SavedVersions { get; } = new();

    public McpToolHarness Harness => _harness ??= McpToolHarness.Create<McpAssemblyMarker>(Register);

    public static McpTestUser Reader(string name = "reader") =>
        McpTestUser.Create(name).WithPermissions(Permissions.Read);

    // builder:read (and the other permissions) in store A only, through the store scope of a role.
    public static McpTestUser StoreAReader(string name = "store-a-reader") =>
        McpTestUser.Create(name).WithScopedPermission(Permissions.Read, StoreScope, StoreA);

    // An account bound to store A: global permissions still reach store A only.
    public McpTestUser StoreBoundReader(string name = "bound-reader")
    {
        BindAccountsTo(StoreA);
        return Reader(name);
    }

    public void BindAccountsTo(string storeId) => _boundStoreId = storeId;

    // The pages the read and edit tools load and the designer's save writes, instead of the mocked page services.
    public Fakes.FakeGroupedPageService UsePageStore()
    {
        PageStore = new Fakes.FakeGroupedPageService();
        return PageStore;
    }

    public ValueTask DisposeAsync() => _harness?.DisposeAsync() ?? ValueTask.CompletedTask;

    // The rows the pages list answers, filtered by store and paged, as the list service does.
    public void ListPages(Func<IEnumerable<SiteBuilderPageListItem>> rows)
    {
        PageList
            .Setup(x => x.SearchAsync(It.IsAny<SiteBuilderPageSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteBuilderPageSearchCriteria criteria, CancellationToken _) =>
            {
                var matches = rows()
                    .Where(x => criteria.StoreId == null || StoreOf(x) == criteria.StoreId)
                    .Where(x => criteria.StoreIds == null || criteria.StoreIds.Contains(StoreOf(x)))
                    .ToList();

                return new SiteBuilderPageListSearchResult
                {
                    TotalCount = matches.Count,
                    Results = matches.Skip(criteria.Skip).Take(criteria.Take).ToList(),
                };
            });
    }

    public static SiteBuilderPageListItem LandingRow(GroupedSiteBuilderPage page) => new()
    {
        Type = "Landing",
        Id = page.Id,
        Name = page.Name,
        Permalink = page.Permalink,
        CreatedDate = page.CreatedDate,
        Page = page,
    };

    // Runs a job of a bulk tool as the job engine would, with the services the module registers.
    public async Task<RecordingJobContext> RunJobAsync<THandler, TPayload>(TPayload payload)
        where THandler : class, VirtoCommerce.Platform.Core.Jobs.IBackgroundJobHandler<TPayload>
        where TPayload : class
    {
        var context = new RecordingJobContext();
        await Harness.Services.GetRequiredService<THandler>().Execute(payload, context, Xunit.TestContext.Current.CancellationToken);
        return context;
    }

    private static string StoreOf(SiteBuilderPageListItem row) => row.Page?.StoreId ?? row.MultilanguagePage?.StoreId;

    public Task<McpToolCallResult> CallAsync(McpTestUser user, string tool, object arguments) =>
        Harness.CallAsync(user, tool, arguments, cancellationToken: Xunit.TestContext.Current.CancellationToken);

    // The groups GetByIdAsync finds.
    public void AddGroups(params GroupedSiteBuilderPage[] groups)
    {
        GroupedPages
            .Setup(x => x.GetAsync(It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IList<string> ids, string _, bool _) => groups.Where(x => ids.Contains(x.Id)).ToList());
    }

    public static GroupedSiteBuilderPage Group(string id, string storeId, string status = "Published") => new()
    {
        Id = id,
        StoreId = storeId,
        Name = $"Page {id}",
        Permalink = $"/{id}",
        CultureName = "en-US",
        CreatedDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        Pages = [new SiteBuilderPage { Id = $"{id}-version", GroupId = id, StoreId = storeId, Status = status }],
    };

    private void Register(IServiceCollection services)
    {
        RegisterPermissionScopes();

        var pageSize = new Mock<IMcpPageSizeResolver>();
        pageSize.Setup(x => x.GetPageSizeAsync(It.IsAny<int?>())).ReturnsAsync((int? take) => Math.Clamp(take ?? 20, 1, 50));

        services.AddAuthorizationCore();
        services.AddSingleton<Func<UserManager<ApplicationUser>>>(CreateUserManager);
        services.AddSingleton<IAuthorizationHandler>(new SiteBuilderAuthorizationHandler(CreateUserManager, Options.Create(CreateJsonOptions())));
        services.AddSingleton<IContentPermissionService>(provider => new ContentPermissionService(provider.GetRequiredService<IAuthorizationService>()));
        services.AddSingleton<IValidator<ContentHistoryRequest>>(new ContentHistoryRequestValidator(
            new SiteBuilderStoreRequestValidator(new FakeStoreService(new Store { Id = StoreA }, new Store { Id = StoreB }, new Store { Id = StoreC }))));

        services
            .AddSingleton(pageSize.Object)
            .AddSingleton(PageList.Object)
            .AddSingleton(PageVersionSearch.Object)
            .AddSingleton(GroupedPages.Object)
            .AddSingleton(PageContents.Object)
            .AddSingleton(AssetSearch.Object)
            .AddSingleton(AssetReferences.Object)
            .AddSingleton(SharedComponentSearch.Object)
            .AddSingleton(SharedComponents.Object)
            .AddSingleton(SharedComponentContents.Object)
            .AddSingleton(SharedComponentUsages.Object)
            .AddSingleton(History.Object);

        if (PageStore != null)
        {
            services
                .AddSingleton<IGroupedPageService>(PageStore)
                .AddSingleton<IGroupedPageContentService>(PageStore);
        }

        services.AddSingleton<IDesignerOperationService>(provider => CreateDesignerOperations(provider.GetRequiredService<IAuthorizationService>()));

        services.AddSingleton<ISiteBuilderSectionScanner>(provider => new SiteBuilderSectionScanner(
            provider.GetRequiredService<ISiteBuilderPageListService>(),
            provider.GetRequiredService<IGroupedPageContentService>(),
            SharedComponents.Object,
            SharedComponentContents.Object,
            new Mock<IThemeStorageFactory>().Object));

        // The bulk tools and their jobs as SiteBuilderMcpRegistration adds them; the job engine is the test's dispatcher.
        var principals = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
        principals
            .Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync((ApplicationUser account) => JobUsers.GetValueOrDefault(account.Id));
        services
            .AddSingleton<IMcpJobDispatcher>(Jobs)
            .AddSingleton(MultilanguagePages.Object)
            .AddSingleton(UserNames.Object)
            .AddSingleton(principals.Object)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>))
            .AddTransient<PageDraftJobRunner>()
            .AddTransient<TranslationSources>()
            .AddTransient<SiteBuilderJobUsers>()
            .AddTransient<UpdateSectionsBulkJob>()
            .AddTransient<SetTranslationsJob>();
    }

    // The designer's save as the module wires it, over the page store; theme files live in a blob storage of the test.
    private DesignerOperationService CreateDesignerOperations(IAuthorizationService authorizationService)
    {
        var blobs = new FakeBlobStorage();
        var themes = new ThemeDraftService(new ThemeStorageFactory(blobs.StoreService, blobs, blobs, blobs), new InProcessDistributedLock(), blobs,
            NullLogger<ThemeDraftService>.Instance);

        return TestServices.DesignerOperations(
            authorizationService: authorizationService,
            contentPermissionService: TestServices.Permissions(authorizationService),
            requestValidator: RequestValidators.Content(StoreA, StoreB, StoreC),
            designerContentService: TestServices.Designer(SavedVersions, themeDrafts: themes, pages: PageStore),
            themeDraftService: themes,
            groupedPageService: PageStore,
            sharedComponentPlacementValidator: new SiteBuilderSharedComponentPlacementValidator(new NoopSharedComponentReferenceIndexService(), authorizationService));
    }

    private UserManager<ApplicationUser> CreateUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store
            .Setup(x => x.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new ApplicationUser { Id = id, StoreId = _boundStoreId });

        return new UserManager<ApplicationUser>(store.Object, null, null, null, null, null, null, null, null);
    }

    // The store scope as the platform registers it, so the handler reads the scoped claims of McpTestUser.
    private static void RegisterPermissionScopes()
    {
        AbstractTypeFactory<PermissionScope>.RegisterType<StoreSelectedScope>();
        PolymorphJsonConverter.RegisterTypeForDiscriminator(typeof(PermissionScope), nameof(PermissionScope.Type));
    }

    private static MvcNewtonsoftJsonOptions CreateJsonOptions()
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.Converters.Add(new PolymorphJsonConverter());
        options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        return options;
    }
}

// The job engine of the tests: records what a bulk tool queues, or fails as a platform without background jobs does.
internal sealed class RecordingJobDispatcher : IMcpJobDispatcher
{
    public List<(Type Handler, object Payload, McpJobRequest Request)> Enqueued { get; } = [];

    public Exception Failure { get; set; }

    public Task<McpJobHandle> EnqueueAsync<THandler>(RequestContext<CallToolRequestParams> context, object payload, McpJobRequest request)
        where THandler : class
    {
        if (Failure != null)
        {
            throw Failure;
        }

        Enqueued.Add((typeof(THandler), payload, request));
        return Task.FromResult(new McpJobHandle { JobId = $"job-{Enqueued.Count}", Status = "Queued", Title = request.Title, TotalCount = request.TotalCount, Preview = request.Preview });
    }
}

internal sealed class RecordingJobContext : VirtoCommerce.Platform.Core.Jobs.IJobExecutionContext, VirtoCommerce.Platform.Core.Jobs.IJobProgress
{
    public List<VirtoCommerce.Platform.Core.Jobs.JobProgressInfo> Reports { get; } = [];

    public string JobId => "engine-job";

    public VirtoCommerce.Platform.Core.Jobs.IJobProgress Progress => this;

    public IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    public string FinalMessage => Reports.Count == 0 ? null : Reports[^1].Message;

    public Task Report(VirtoCommerce.Platform.Core.Jobs.JobProgressInfo progress, CancellationToken cancellationToken = default)
    {
        Reports.Add(progress);
        return Task.CompletedTask;
    }
}
