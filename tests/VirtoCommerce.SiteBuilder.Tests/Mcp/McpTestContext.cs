using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
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
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using VirtoCommerce.SiteBuilder.MCP.Services;
using VirtoCommerce.CustomerModule.Core.Services;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.StoreModule.Core.Services;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Security;
using Fakes = VirtoCommerce.SiteBuilder.Tests.PublishedRenameContentPreservationTests;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

internal sealed class McpTestContext : IAsyncDisposable
{
    public const string StoreA = "store-a";
    public const string StoreB = "store-b";
    public const string StoreC = "store-c";
    public const string StoreScope = nameof(StoreSelectedScope);
    public const string AssetRead = "platform:asset:read";

    private string _boundStoreId;
    private McpToolHarness _harness;
    private (DesignerContentService Designer, ThemeDraftService Themes) _designer;
    private ThemeStorageFactory _themeStorage;

    // The platform sets these at startup; without them the handler and the bulk tools find no user id.
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

    public Dictionary<string, ClaimsPrincipal> JobUsers { get; } = new(StringComparer.Ordinal);

    public HashSet<string> DeletedUsers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> LockedOutUsers { get; } = new(StringComparer.Ordinal);

    public Fakes.FakeGroupedPageService PageStore { get; private set; }

    public RecordingContentHistory SavedVersions { get; } = new();

    public FakeBlobStorage Blobs { get; } = new();

    public FakeStoreService Stores { get; } = new(
        new Store { Id = StoreA, DefaultLanguage = "en-US", Languages = ["en-US"], DynamicProperties = [] },
        new Store { Id = StoreB, DefaultLanguage = "en-US", Languages = ["en-US"], DynamicProperties = [] },
        new Store { Id = StoreC, DefaultLanguage = "en-US", Languages = ["en-US"], DynamicProperties = [] });

    public Mock<ISiteBuilderPreviewQueryService> Previews { get; } = new();

    public Mock<IFilePageOperationService> FilePageOperations { get; } = new();

    public Mock<ISiteBuilderPublishStatusQueryService> PublishStatuses { get; } = new();

    // Git serves version listing only; file page reads and writes stay in the test's blob storage.
    public bool GitFlow { get; set; }

    public Mock<IGitContentHistory> GitHistory { get; } = new();

    public Mock<IStoreSearchService> StoreSearch { get; } = new();

    public Mock<ISettingsManager> Settings { get; } = new();

    public Mock<IMemberSearchService> Members { get; } = new();

    public IGroupedPageOperationService GroupedPageOperations { get; set; }

    public Action BeforeDesignerWrite { get; set; }

    public McpToolHarness Harness => _harness ??= McpToolHarness.Create(services => services.AddSiteBuilderMcp(), Register, scope: null);

    public static McpTestUser Reader(string name = "reader") =>
        McpTestUser.Create(name).WithPermissions(Permissions.Read);

    public static McpTestUser StoreAReader(string name = "store-a-reader") =>
        McpTestUser.Create(name).WithScopedPermission(Permissions.Read, StoreScope, StoreA);

    // The designer's save checks the update permission globally, so only the read permission is ever store-scoped.
    public static McpTestUser Editor(string name = "editor") =>
        McpTestUser.Create(name).WithPermissions(Permissions.Read, Permissions.Update);

    public static McpTestUser StoreAEditor() =>
        McpTestUser.Create("store-a-editor").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update);

    public static McpTestUser Publisher() =>
        McpTestUser.Create("publisher").WithPermissions(Permissions.Read, Permissions.Publish);

    public static McpTestUser StoreAPublisher() =>
        McpTestUser.Create("store-a-publisher").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Publish);

    public McpTestUser StoreBoundReader(string name = "bound-reader")
    {
        BindAccountsTo(StoreA);
        return Reader(name);
    }

    public void BindAccountsTo(string storeId) => _boundStoreId = storeId;

    public Fakes.FakeGroupedPageService UsePageStore()
    {
        PageStore = new Fakes.FakeGroupedPageService();
        return PageStore;
    }

    public ValueTask DisposeAsync() => _harness?.DisposeAsync() ?? ValueTask.CompletedTask;

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

    public GroupedSiteBuilderPage SeedDraft(string id, string storeId, string content, string language = "en-US", string multilanguagePageId = null,
        DateTime? sourceRevision = null)
    {
        var group = new GroupedSiteBuilderPage
        {
            Id = id,
            StoreId = storeId,
            Name = $"Page {id}",
            Permalink = $"/{id}",
            CultureName = language,
            MultilanguagePageId = multilanguagePageId,
            SourceRevisionDate = sourceRevision,
            Pages = [new SiteBuilderPage { Id = $"{id}-draft", GroupId = id, StoreId = storeId, Status = "Draft" }],
        };
        PageStore.SeedGroup(group);
        PageStore.SeedContent($"{id}-draft", content);
        return group;
    }

    public async Task<SiteBuilderContentSnapshot> LoadDraftAsync(string id) =>
        await PageStore.LoadGroupContentAsync(await PageStore.GetByIdAsync(id), cancellationToken: Xunit.TestContext.Current.CancellationToken);

    public async Task<string> DraftContentAsync(string id) => (await LoadDraftAsync(id)).Content;

    public async Task<string> DraftVersionAsync(string id) => (await LoadDraftAsync(id)).ETag;

    public static SiteBuilderChangePreview Preview(string field, string from, string to, string objectType = "page", string objectId = null,
        bool affectsPublished = false, string eTag = null) => new()
        {
            AffectsPublished = affectsPublished,
            Objects =
            [
                new SiteBuilderObjectChange
                {
                    ObjectType = objectType,
                    ObjectId = objectId,
                    ETag = eTag,
                    Fields = [new SiteBuilderFieldChange { Name = field, From = from, To = to }],
                },
            ],
        };

    public const string SampleContent = """{ "settings": { "name": "Home" }, "content": [ { "id": "hero", "type": "hero-banner", "title": "Spring" } ] }""";

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

    public static readonly McpToolCallOptions Approved = new() { AsApprovedChange = true };

    public async Task<Dictionary<string, object>> ReviewAsync(McpTestUser user, string tool, Dictionary<string, object> arguments)
    {
        var review = (await CallAsync(user, tool, new Dictionary<string, object>(arguments) { ["dryRun"] = true })).EnsureSucceeded().Read<SiteBuilderChange>();
        var call = new Dictionary<string, object>(arguments) { ["expected_versions"] = review.ExpectedVersions };
        if (review.DraftOwner != null)
        {
            call["draft_owner"] = review.DraftOwner;
        }

        return call;
    }

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

        services.AddSingleton<IDesignerOperationService>(provider =>
            new HookedDesignerOperations(CreateDesignerOperations(provider.GetRequiredService<IAuthorizationService>()), this));
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

        services
            .AddSingleton<IThemeStorageFactory>(ThemeStorage)
            .AddSingleton<ISchemaCatalogService>(new SchemaCatalogService(ThemeStorage))
            .AddSingleton<IThemePaletteService>(new ThemePaletteService())
            .AddSingleton<ICustomSectionValidator>(TestServices.CustomSectionValidator())
            .AddSingleton<ICustomSectionService>(new CustomSectionService(ThemeStorage, GetDesigner().Themes))
            .AddSingleton(Previews.Object)
            .AddSingleton<IThemeOperationService>(provider => new ThemeOperationService(provider.GetRequiredService<IAuthorizationService>(),
                RequestValidators.Stores(StoreA, StoreB, StoreC), CreateThemePublisher(), SavedVersions))
            .AddSingleton<ISiteBuilderContentQueryService>(provider => new SiteBuilderContentQueryService(provider.GetRequiredService<IAuthorizationService>(),
                RequestValidators.Content(StoreA, StoreB, StoreC), Blobs, Blobs, Blobs, ThemeStorage, new FakeGitContentPolicy(enabled: GitFlow), null,
                GitHubConnections.CreateService(new GitContentOptions()), Options.Create(new GitContentOptions()), GitHistory.Object));

        var principals = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
        principals
            .Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync((ApplicationUser account) => JobUsers[account.Id]);
        services
            .AddSingleton<IMcpJobDispatcher>(Jobs)
            .AddSingleton(MultilanguagePages.Object)
            .AddSingleton(UserNames.Object)
            .AddSingleton(principals.Object)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services
            .AddSingleton<IStoreService>(Stores)
            .AddSingleton(StoreSearch.Object)
            .AddSingleton(Settings.Object)
            .AddSingleton(Members.Object);
    }

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

    private ContentHistoryOperationService CreateHistoryOperations(IAuthorizationService authorizationService) =>
        new(authorizationService, History.Object, PageStore ?? GroupedPages.Object, GetDesigner().Designer, TestServices.Permissions(authorizationService),
            new SiteBuilderSharedComponentPlacementValidator(new NoopSharedComponentReferenceIndexService(), authorizationService),
            RequestValidators.Stores(StoreA, StoreB, StoreC), RequestValidators.History(StoreA, StoreB, StoreC), RequestValidators.Restore(StoreA, StoreB, StoreC));

    private GroupedPageOperationService CreateGroupedPageOperations(IAuthorizationService authorizationService)
    {
        var pageService = new Fakes.FakeSiteBuilderPageService(PageStore);
        var contentService = new SiteBuilderPageContentService(pageService, PageStore, new Fakes.NoopEventPublisher(),
            NullLogger<SiteBuilderPageContentService>.Instance);

        return TestServices.GroupedPageOperations(authorizationService, PageStore, contentService, pageService,
            designerContentService: GetDesigner().Designer, contentHistory: SavedVersions,
            publishConflictService: new SiteBuilderPublishConflictService(PageStore, PageStore, Stores, distributedLock: null));
    }

    private (DesignerContentService Designer, ThemeDraftService Themes) GetDesigner()
    {
        if (_designer == default)
        {
            var themes = new ThemeDraftService(ThemeStorage, Lock, Blobs, NullLogger<ThemeDraftService>.Instance);
            var filePages = TestServices.FilePageDrafts(Options.Create(new GitContentOptions()), connection: null, policy: new FakeGitContentPolicy(enabled: false),
                storeService: Stores, pathResolver: Blobs, blobs: Blobs, publishing: Blobs, events: Blobs);
            _designer = (TestServices.Designer(SavedVersions, filePageDrafts: filePages, themeDrafts: themes, pages: PageStore, themePublisher: CreateThemePublisher(),
                catalogs: new SchemaCatalogService(ThemeStorage), themes: ThemeStorage), themes);
        }

        return _designer;
    }

    private InProcessDistributedLock Lock { get; } = new();

    public ThemeStorageFactory ThemeStorage => _themeStorage ??= new ThemeStorageFactory(Stores, Blobs, Blobs, Blobs);

    private ThemePublisher CreateThemePublisher() =>
        new(ThemeStorage, new ThemeSnapshotBuilder(), TestServices.ThemeValidator(), Stores, new SchemaCatalogService(ThemeStorage), Lock, Blobs, new NoContentHistory(),
            Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, TestServices.ContentGuard, new ThemePaletteService());

    public Task SeedCatalogAsync(string storeId, string[] sections, string paletteTokens = null, string kit = null)
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

        if (kit != null)
        {
            catalog["kit"] = Newtonsoft.Json.Linq.JObject.Parse(kit);
        }

        return new SchemaCatalogService(ThemeStorage).SaveAsync(storeId, catalog.ToString(), Xunit.TestContext.Current.CancellationToken);
    }

    public const string ProductTemplate = """{ "settings": {}, "content": [ { "id": "main", "type": "product-info" }, { "id": "reviews", "type": "product-reviews" } ] }""";

    public static readonly string[] ThemeSections =
        ["product-info", "product-reviews", "category-listing", "page-content", "site-header", "site-footer", "announcement-bar", "text"];

    // Part of the storefront's own kit: what a custom section's template may use besides HTML.
    public const string StorefrontKit = """
        {
          "tags": {
            "vc-button": { "description": "A button; with to, a link that looks like one.", "attributes": { "label": { "type": "string" }, "to": { "type": "link" } } }
          },
          "behaviors": {
            "inview": {
              "description": "Sets data-visible while the element is on screen.",
              "options": { "threshold": { "type": "number", "min": 0, "max": 1 }, "once": { "type": "boolean" } },
              "writes": ["data-visible"]
            },
            "count-up": {
              "description": "The text counts up to the number when the element first shows.",
              "value": { "type": "number" },
              "options": { "duration": { "type": "number", "min": 0, "max": 5000 }, "decimals": { "type": "number", "min": 0, "max": 4 } },
              "writes": ["text"]
            }
          }
        }
        """;

    public const string StatsSectionType = """
        {
          "format": 1,
          "name": "Stats",
          "schema": { "settings": [ { "id": "title", "type": "string", "translatable": true } ] },
          "template": "<section><h2>{{ settings.title }}</h2></section>",
          "css": ""
        }
        """;

    public async Task SeedThemeAsync(string storeId, string kit = null)
    {
        var tokens = new Newtonsoft.Json.Linq.JObject
        {
            ["light"] = new Newtonsoft.Json.Linq.JArray(StorefrontPaletteTokens.Light),
            ["dark"] = new Newtonsoft.Json.Linq.JArray(StorefrontPaletteTokens.Dark),
        };
        await SeedCatalogAsync(storeId, ThemeSections, tokens.ToString(), kit);
        SeedThemeFile(storeId, "templates/product.json", ProductTemplate);
    }

    public void SeedThemeFile(string storeId, string pathInTheme, string content, bool draft = false) =>
        Blobs.Files[ThemeFileKey(storeId, pathInTheme, draft)] = content;

    public string ReadThemeFile(string storeId, string pathInTheme, bool draft = false) =>
        Blobs.Files.GetValueOrDefault(ThemeFileKey(storeId, pathInTheme, draft));

    public void SeedFilePage(string storeId, string path, string content, bool draft = false) =>
        Blobs.Files[FilePageKey(storeId, path, draft)] = content;

    public string ReadFilePage(string storeId, string path, bool draft = false) =>
        Blobs.Files.GetValueOrDefault(FilePageKey(storeId, path, draft));

    public static string FilePageKey(string storeId, string path, bool draft = false) => $"pages/{storeId}/{path}{(draft ? "-draft" : string.Empty)}";

    public static string ThemeFileKey(string storeId, string pathInTheme, bool draft = false) =>
        $"themes/{storeId}/default/{pathInTheme}{(draft ? "-draft" : string.Empty)}";

    private sealed class HookedDesignerOperations(IDesignerOperationService inner, McpTestContext context) : IDesignerOperationService
    {
        public Task<SiteBuilderOperationResult<ThemeFileContent>> ReadThemeFileAsync(DesignerThemeFileRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => inner.ReadThemeFileAsync(request, user, cancellationToken);

        public Task<SiteBuilderOperationResult<DesignerSaveResult>> SaveAsync(DesignerSaveOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default)
        {
            if (!request.DryRun)
            {
                context.BeforeDesignerWrite?.Invoke();
            }

            return inner.SaveAsync(request, user, cancellationToken);
        }

        public Task<SiteBuilderOperationResult<ContentSaveResult>> DiscardAsync(DesignerDiscardOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => inner.DiscardAsync(request, user, cancellationToken);

        public Task<SiteBuilderOperationResult<IDictionary<string, IList<string>>>> ValidateThemeFilesAsync(string storeId, IList<SiteBuilderContentFile> files,
            bool draft, string catalogHash, CancellationToken cancellationToken = default) => inner.ValidateThemeFilesAsync(storeId, files, draft, catalogHash, cancellationToken);
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

internal sealed class RecordingJobDispatcher : IMcpJobDispatcher
{
    public List<(Type Handler, object Payload, McpJobRequest Request)> Enqueued { get; } = [];

    // A set failure stands for a platform without background jobs.
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
