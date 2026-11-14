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
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.JsonConverters;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SiteBuilder.Core.GitContent;
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
using VirtoCommerce.SiteBuilder.MCP.Tools;
using VirtoCommerce.CustomerModule.Core.Services;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.StoreModule.Core.Services;
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
    private (DesignerContentService Designer, ThemeDraftService Themes) _designer;
    private ThemeStorageFactory _themeStorage;

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
    public Mock<IMultilanguagePageOperationService> MultilanguagePageOperations { get; } = new();
    public Mock<IUserNameResolver> UserNames { get; } = new();
    public RecordingJobDispatcher Jobs { get; } = new();

    // The accounts a job runs as, by user id, as a sign-in would give them when the job runs; a missing one no longer exists.
    public Dictionary<string, ClaimsPrincipal> JobUsers { get; } = new(StringComparer.Ordinal);

    // Accounts the user store no longer finds, and accounts locked out until a time ahead.
    public HashSet<string> DeletedUsers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> LockedOutUsers { get; } = new(StringComparer.Ordinal);

    // Pages with their content, saved through the designer's own save; null unless a test calls UsePageStore.
    public Fakes.FakeGroupedPageService PageStore { get; private set; }

    public RecordingContentHistory SavedVersions { get; } = new();

    // The theme files of every store, as "themes/{storeId}/{theme}/{path}", drafts with "-draft".
    public FakeBlobStorage Blobs { get; } = new();

    public FakeStoreService Stores { get; } = new(
        new Store { Id = StoreA, DefaultLanguage = "en-US", Languages = ["en-US"], DynamicProperties = [] },
        new Store { Id = StoreB, DefaultLanguage = "en-US", Languages = ["en-US"], DynamicProperties = [] },
        new Store { Id = StoreC, DefaultLanguage = "en-US", Languages = ["en-US"], DynamicProperties = [] });

    public Mock<ISiteBuilderPreviewQueryService> Previews { get; } = new();

    // The writes of pages stored as files: git is the operation service's own business, tested with it.
    public Mock<IFilePageOperationService> FilePageOperations { get; } = new();

    public Mock<ISiteBuilderPublishStatusQueryService> PublishStatuses { get; } = new();

    // The stores keep their pages stored as files in git: the history of such a page is read from GitHistory. Reads and
    // writes of the pages themselves stay in the blob storage of the test, so a test that sets it only lists versions.
    public bool GitFlow { get; set; }

    public Mock<IGitContentHistory> GitHistory { get; } = new();

    // Every store, for a user who reads all of them: the overview pages through the stores the store search finds.
    public Mock<IStoreSearchService> StoreSearch { get; } = new();

    public Mock<ISettingsManager> Settings { get; } = new();

    public Mock<IMemberSearchService> Members { get; } = new();

    // The page writes the tools run; without one, the module's operation service over the page store.
    public IGroupedPageOperationService GroupedPageOperations { get; set; }

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

    // A page of the page store with a published version, a draft, or both, each holding the content.
    public GroupedSiteBuilderPage SeedPage(string id, string storeId, bool published, bool draft, string content = SampleContent)
    {
        var group = new GroupedSiteBuilderPage
        {
            Id = id,
            StoreId = storeId,
            Name = $"Page {id}",
            Permalink = $"/{id}",
            CultureName = "en-US",
            Visibility = true,
        };

        if (published)
        {
            group.Pages.Add(new SiteBuilderPage { Id = $"{id}-published", GroupId = id, StoreId = storeId, Status = "Published", ModifiedDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) });
        }

        if (draft)
        {
            group.Pages.Add(new SiteBuilderPage { Id = $"{id}-draft", GroupId = id, StoreId = storeId, Status = "Draft", ModifiedDate = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc) });
        }

        PageStore.SeedGroup(group);
        foreach (var page in group.Pages)
        {
            PageStore.SeedContent(page.Id, page.Status == "Draft" ? content.Replace("Spring", "Summer", StringComparison.Ordinal) : content);
        }

        return group;
    }

    public const string SampleContent = """{ "settings": { "name": "Home" }, "content": [ { "id": "hero", "type": "hero-banner", "title": "Spring" } ] }""";

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
    // onReport runs after each report of progress, as between two pages.
    public async Task<RecordingJobContext> RunJobAsync<THandler, TPayload>(TPayload payload, Action<int> onReport = null)
        where THandler : class, VirtoCommerce.Platform.Core.Jobs.IBackgroundJobHandler<TPayload>
        where TPayload : class
    {
        var context = new RecordingJobContext { OnReport = onReport };
        await Harness.Services.GetRequiredService<THandler>().Execute(payload, context, Xunit.TestContext.Current.CancellationToken);
        return context;
    }

    private static string StoreOf(SiteBuilderPageListItem row) => row.Page?.StoreId ?? row.MultilanguagePage?.StoreId;

    public Task<McpToolCallResult> CallAsync(McpTestUser user, string tool, object arguments, McpToolCallOptions options = null) =>
        Harness.CallAsync(user, tool, arguments, options, Xunit.TestContext.Current.CancellationToken);

    // The groups and the language versions the multilanguage page service finds.
    public void AddMultilanguagePages(params MultilanguagePage[] roots)
    {
        MultilanguagePages
            .Setup(x => x.GetAsync(It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IList<string> ids, string _, bool _) => roots.Where(x => ids.Contains(x.Id)).ToList());
    }

    public static MultilanguagePage Root(string id, string storeId) => new()
    {
        Id = id,
        StoreId = storeId,
        Name = $"Root {id}",
        Permalink = $"/{id}",
        DefaultCultureName = "en-US",
        Visibility = true,
    };

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

        // The platform's permission policies, which the module's services ask by name: granted globally, or to an administrator.
        services.AddAuthorizationCore(options =>
        {
            foreach (var permission in Permissions.AllPermissions)
            {
                options.AddPolicy(permission, policy => policy.RequireAssertion(x =>
                    x.User.IsInRole(PlatformConstants.Security.SystemRoles.Administrator) ||
                    x.User.HasClaim(PlatformConstants.Security.Claims.PermissionClaimType, permission)));
            }
        });
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
        services.AddSingleton<IContentHistoryOperationService>(provider => CreateHistoryOperations(provider.GetRequiredService<IAuthorizationService>()));
        services.AddSingleton<ISiteBuilderSharedComponentOperationService>(provider => TestServices.SharedComponentOperations(
            provider.GetRequiredService<IAuthorizationService>(), SharedComponents.Object, SharedComponentContents.Object, SharedComponentUsages.Object, Stores,
            SharedComponentSearch.Object));
        services
            .AddSingleton(FilePageOperations.Object)
            .AddSingleton(PublishStatuses.Object);
        services.AddSingleton(provider => GroupedPageOperations ?? CreateGroupedPageOperations(provider.GetRequiredService<IAuthorizationService>()));
        services.AddSingleton(MultilanguagePageOperations.Object);

        services.AddSingleton<ISiteBuilderSectionScanner>(provider => new SiteBuilderSectionScanner(
            provider.GetRequiredService<ISiteBuilderPageListService>(),
            provider.GetRequiredService<IGroupedPageContentService>(),
            SharedComponents.Object,
            SharedComponentContents.Object,
            ThemeStorage));

        // The theme as the designer and the publication keep it, in the blob storage of the test.
        services
            .AddSingleton<IThemeStorageFactory>(ThemeStorage)
            .AddSingleton<ISchemaCatalogService>(new SchemaCatalogService(ThemeStorage))
            .AddSingleton<IThemePaletteService>(new ThemePaletteService())
            .AddSingleton(Previews.Object)
            .AddSingleton<IThemeOperationService>(provider => new ThemeOperationService(provider.GetRequiredService<IAuthorizationService>(),
                RequestValidators.Stores(StoreA, StoreB, StoreC), new NewSiteBuilderStores(), CreateThemePublisher(), SavedVersions))
            .AddSingleton<ISiteBuilderContentQueryService>(provider => new SiteBuilderContentQueryService(provider.GetRequiredService<IAuthorizationService>(),
                RequestValidators.Content(StoreA, StoreB, StoreC), Blobs, Blobs, Blobs, ThemeStorage, new FakeGitContentPolicy(enabled: GitFlow), null,
                GitHubConnections.CreateService(new GitContentOptions()), Options.Create(new GitContentOptions()), GitHistory.Object));

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
            .AddTransient<SetTranslationsJob>()
            .AddTransient<SiteThemeStates>()
            .AddTransient<PublicationDryRuns>();

        // The stores, settings and members the overview and the resources read.
        services
            .AddSingleton<IStoreService>(Stores)
            .AddSingleton(StoreSearch.Object)
            .AddSingleton(Settings.Object)
            .AddSingleton(Members.Object);
    }

    // The designer's save as the module wires it, over the page store; theme files live in a blob storage of the test.
    private DesignerOperationService CreateDesignerOperations(IAuthorizationService authorizationService)
    {
        var (designer, themes) = GetDesigner();

        return TestServices.DesignerOperations(
            authorizationService: authorizationService,
            contentPermissionService: TestServices.Permissions(authorizationService),
            requestValidator: RequestValidators.Content(StoreA, StoreB, StoreC),
            designerContentService: designer,
            themeDraftService: themes,
            groupedPageService: PageStore,
            sharedComponentPlacementValidator: new SiteBuilderSharedComponentPlacementValidator(new NoopSharedComponentReferenceIndexService(), authorizationService));
    }

    // The history's restore as the module wires it, over the page store and the designer; the versions come from History.
    private ContentHistoryOperationService CreateHistoryOperations(IAuthorizationService authorizationService) =>
        new(authorizationService, History.Object, PageStore ?? GroupedPages.Object, GetDesigner().Designer, TestServices.Permissions(authorizationService),
            new SiteBuilderSharedComponentPlacementValidator(new NoopSharedComponentReferenceIndexService(), authorizationService),
            RequestValidators.Stores(StoreA, StoreB, StoreC), RequestValidators.History(StoreA, StoreB, StoreC), RequestValidators.Restore(StoreA, StoreB, StoreC));

    // The page group writes as the module wires them, over the page store.
    private GroupedPageOperationService CreateGroupedPageOperations(IAuthorizationService authorizationService)
    {
        var pageService = new Fakes.FakeSiteBuilderPageService(PageStore);
        var contentService = new SiteBuilderPageContentService(pageService, PageStore, new Fakes.NoopEventPublisher(),
            NullLogger<SiteBuilderPageContentService>.Instance);

        return TestServices.GroupedPageOperations(authorizationService, PageStore, contentService, pageService,
            designerContentService: GetDesigner().Designer, contentHistory: SavedVersions);
    }

    private (DesignerContentService Designer, ThemeDraftService Themes) GetDesigner()
    {
        if (_designer == default)
        {
            var themes = new ThemeDraftService(ThemeStorage, Lock, Blobs, NullLogger<ThemeDraftService>.Instance);
            // Pages stored as files are kept in the blob storage of the test: no store of the test keeps them in git.
            var filePages = TestServices.FilePageDrafts(Options.Create(new GitContentOptions()), connection: null, policy: new FakeGitContentPolicy(enabled: false),
                storeService: Stores, pathResolver: Blobs, blobs: Blobs, publishing: Blobs, events: Blobs);
            _designer = (TestServices.Designer(SavedVersions, filePageDrafts: filePages, themeDrafts: themes, pages: PageStore, themePublisher: CreateThemePublisher(),
                themeSnapshots: new NewSiteBuilderStores(), catalogs: new SchemaCatalogService(ThemeStorage)), themes);
        }

        return _designer;
    }

    private InProcessDistributedLock Lock { get; } = new();

    public ThemeStorageFactory ThemeStorage => _themeStorage ??= new ThemeStorageFactory(Stores, Blobs, Blobs, Blobs);

    private ThemePublisher CreateThemePublisher() =>
        new(ThemeStorage, new ThemeSnapshotBuilder(), new ThemeValidator(), Stores, new SchemaCatalogService(ThemeStorage), Lock, Blobs, new NoContentHistory(),
            Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, TestServices.ContentGuard, new ThemePaletteService());

    // The schema catalog a storefront preview sends: the section types the theme may hold, and its palette tokens.
    public Task SeedCatalogAsync(string storeId, string[] sections, string paletteTokens = null)
    {
        var catalog = new Newtonsoft.Json.Linq.JObject
        {
            ["hash"] = $"catalog-{storeId}",
            ["sections"] = new Newtonsoft.Json.Linq.JObject(sections.Select(x => new Newtonsoft.Json.Linq.JProperty(x, new Newtonsoft.Json.Linq.JObject()))),
        };
        if (paletteTokens != null)
        {
            catalog["paletteTokens"] = Newtonsoft.Json.Linq.JObject.Parse(paletteTokens);
        }

        return new SchemaCatalogService(ThemeStorage).SaveAsync(storeId, catalog.ToString(), Xunit.TestContext.Current.CancellationToken);
    }

    public const string ProductTemplate = """{ "settings": {}, "content": [ { "id": "main", "type": "product-info" }, { "id": "reviews", "type": "product-reviews" } ] }""";

    public static readonly string[] ThemeSections =
        ["product-info", "product-reviews", "category-listing", "page-content", "site-header", "site-footer", "announcement-bar", "text"];

    // A store's theme as a storefront preview leaves it: the catalog with the storefront's palette tokens, and a live
    // product template.
    public async Task SeedThemeAsync(string storeId)
    {
        var tokens = new Newtonsoft.Json.Linq.JObject
        {
            ["light"] = new Newtonsoft.Json.Linq.JArray(StorefrontPaletteTokens.Light),
            ["dark"] = new Newtonsoft.Json.Linq.JArray(StorefrontPaletteTokens.Dark),
        };
        await SeedCatalogAsync(storeId, ThemeSections, tokens.ToString());
        SeedThemeFile(storeId, "templates/product.json", ProductTemplate);
    }

    // A theme file of the store's default theme: its live copy, or its draft.
    public void SeedThemeFile(string storeId, string pathInTheme, string content, bool draft = false) =>
        Blobs.Files[ThemeFileKey(storeId, pathInTheme, draft)] = content;

    public string ReadThemeFile(string storeId, string pathInTheme, bool draft = false) =>
        Blobs.Files.GetValueOrDefault(ThemeFileKey(storeId, pathInTheme, draft));

    // A page stored as a file in the store's blob storage: what is published, or the draft next to it.
    public void SeedFilePage(string storeId, string path, string content, bool draft = false) =>
        Blobs.Files[FilePageKey(storeId, path, draft)] = content;

    public string ReadFilePage(string storeId, string path, bool draft = false) =>
        Blobs.Files.GetValueOrDefault(FilePageKey(storeId, path, draft));

    public static string FilePageKey(string storeId, string path, bool draft = false) => $"pages/{storeId}/{path}{(draft ? "-draft" : string.Empty)}";

    public static string ThemeFileKey(string storeId, string pathInTheme, bool draft = false) =>
        $"themes/{storeId}/default/{pathInTheme}{(draft ? "-draft" : string.Empty)}";

    // Every store of the test uses the new Site Builder, which publishes its theme from drafts.
    private sealed class NewSiteBuilderStores : IThemeSnapshotService
    {
        public Task<ThemeSnapshot> GetPublishedAsync(string storeId, string cultureName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsNewSiteBuilderEnabledAsync(string storeId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private UserManager<ApplicationUser> CreateUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store
            .Setup(x => x.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => DeletedUsers.Contains(id) ? null : new ApplicationUser { Id = id, StoreId = _boundStoreId });
        var lockouts = store.As<IUserLockoutStore<ApplicationUser>>();
        lockouts
            .Setup(x => x.GetLockoutEnabledAsync(It.IsAny<ApplicationUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        lockouts
            .Setup(x => x.GetLockoutEndDateAsync(It.IsAny<ApplicationUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApplicationUser account, CancellationToken _) => LockedOutUsers.Contains(account.Id) ? DateTimeOffset.UtcNow.AddHours(1) : null);

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

    public Action<int> OnReport { get; init; }

    public Task Report(VirtoCommerce.Platform.Core.Jobs.JobProgressInfo progress, CancellationToken cancellationToken = default)
    {
        Reports.Add(progress);
        OnReport?.Invoke(Reports.Count);
        return Task.CompletedTask;
    }
}
