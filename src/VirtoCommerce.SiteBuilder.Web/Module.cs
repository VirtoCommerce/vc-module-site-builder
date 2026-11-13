using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using GraphQL.MicrosoftDI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtoCommerce.ContentModule.Core.Extensions;
using VirtoCommerce.ContentModule.Core.Search;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Extensions;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Data.BackgroundJobs;
using VirtoCommerce.SiteBuilder.Data.ContentProviders;
using VirtoCommerce.SiteBuilder.Data.ExportImport;
using VirtoCommerce.SiteBuilder.Data.GitContent;
using VirtoCommerce.SiteBuilder.Data.Handlers;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.SiteBuilder.Data.Search;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.SiteBuilder.MCP;
using VirtoCommerce.SiteBuilder.Xapi;
using VirtoCommerce.SiteBuilder.Xapi.Extensions;
using VirtoCommerce.Pages.Core.ContentProviders;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.ExportImport;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.StoreModule.Core.Events;
using VirtoCommerce.StoreModule.Core.Security;
using VirtoCommerce.Xapi.Core.Extensions;
using VirtoCommerce.Xapi.Core.Infrastructure;

namespace VirtoCommerce.SiteBuilder.Web
{
    public class Module : IModule, IExportSupport, IImportSupport, IHasConfiguration, IHasModuleService
    {
        // The MCP module is an optional dependency: when it is installed, it initializes before this module. Its contracts
        // (VirtoCommerce.MCP.Core and the MCP SDK) are packaged with this module (module.keep), so they load without it.
        private const string McpModuleId = "VirtoCommerce.MCP";

        private IApplicationBuilder _appBuilder;
        private List<RenamedConfigurationKey> _pageBuilderConfigurationKeys = [];

        public ManifestModuleInfo ModuleInfo { get; set; }
        public IConfiguration Configuration { get; set; }
        public IModuleService ModuleService { get; set; }

        public void Initialize(IServiceCollection serviceCollection)
        {
            serviceCollection.AddSiteBuilderRepositories(Configuration, ModuleInfo);

            // Register services
            serviceCollection.AddTransient<ISiteBuilderPageService, SiteBuilderPageService>();
            serviceCollection.AddTransient<ISiteBuilderPageSearchService, SiteBuilderPageSearchService>();
            serviceCollection.AddTransient<ISiteBuilderPageChangeService, SiteBuilderPageChangeService>();

            serviceCollection.AddTransient<SiteBuilderPageChangedEventHandler>();

            serviceCollection.AddTransient<IGroupedPageService, GroupedPageService>();
            serviceCollection.AddTransient<IGroupedPageContentService>(provider =>
                provider.GetRequiredService<IGroupedPageService>() as IGroupedPageContentService
                ?? ActivatorUtilities.CreateInstance<GroupedPageService>(provider));
            serviceCollection.AddTransient<IGroupedPageSearchService, GroupedPageSearchService>();
            serviceCollection.AddTransient<IMultilanguagePageService, MultilanguagePageService>();
            serviceCollection.AddTransient<IMultilanguagePageOperationService, MultilanguagePageOperationService>();
            serviceCollection.AddTransient<IMultilanguagePageSearchService, MultilanguagePageSearchService>();
            serviceCollection.AddTransient<ISiteBuilderPageListService, SiteBuilderPageListService>();
            serviceCollection.AddTransient<ISiteBuilderAssetReferenceService, SiteBuilderAssetReferenceService>();
            serviceCollection.AddTransient<ISiteBuilderAssetSearchService, SiteBuilderAssetSearchService>();
            serviceCollection.AddTransient<ISiteBuilderAssetReferenceIndexService, SiteBuilderAssetReferenceIndexService>();
            serviceCollection.AddTransient<ISiteBuilderSharedComponentService, SiteBuilderSharedComponentService>();
            serviceCollection.AddTransient<ISiteBuilderSharedComponentSearchService, SiteBuilderSharedComponentSearchService>();
            serviceCollection.AddTransient<ISiteBuilderSharedComponentContentService, SiteBuilderSharedComponentContentService>();
            serviceCollection.AddTransient<ISiteBuilderSharedComponentResolver, SiteBuilderSharedComponentResolver>();
            serviceCollection.AddTransient<ISiteBuilderSharedComponentReferenceIndexService, SiteBuilderSharedComponentReferenceIndexService>();
            serviceCollection.AddTransient<ISiteBuilderSharedComponentPlacementValidator, SiteBuilderSharedComponentPlacementValidator>();
            serviceCollection.AddTransient<ISiteBuilderSharedComponentUsageService, SiteBuilderSharedComponentUsageService>();
            serviceCollection.AddTransient<
                ISiteBuilderSharedComponentAssetReferenceIndexService,
                SiteBuilderSharedComponentAssetReferenceIndexService>();
            serviceCollection.AddTransient<SiteBuilderPageContentService>();
            serviceCollection.AddTransient<IGroupedPageOperationService, GroupedPageOperationService>();
            serviceCollection.AddTransient<IGroupedPageContentWriter, GroupedPageContentWriter>();
            // Site Builder's own: another module registers the platform's IHtmlSanitizer with its own profile
            serviceCollection.AddSingleton<ISiteBuilderHtmlSanitizer, SiteBuilderHtmlSanitizer>();
            serviceCollection.AddSingleton<ISiteBuilderContentGuard, SiteBuilderContentGuard>();

            serviceCollection.AddTransient<GroupedSiteBuilderPageChangedEventHandler>();
            serviceCollection.AddTransient<SiteBuilderSharedComponentContentChangedEventHandler>();
            serviceCollection.AddBackgroundJob<SiteBuilderSharedComponentContentPropagationJob, SiteBuilderSharedComponentContentPropagationJobPayload>(triggerable: false);

            serviceCollection.AddTransient<IAuthorizationHandler, SiteBuilderAuthorizationHandler>();
            serviceCollection.AddTransient<IValidator<SiteBuilderContentFile>, SiteBuilderContentFileValidator>();
            serviceCollection.AddTransient<IValidator<SiteBuilderContentRequest>, SiteBuilderContentRequestValidator>();
            serviceCollection.AddTransient<IValidator<DesignerSavePage>, DesignerPageValidator>();
            serviceCollection.AddTransient<IValidator<DesignerSaveRequest>, DesignerSaveRequestValidator>();
            serviceCollection.AddTransient<IValidator<DesignerDiscardRequest>, DesignerDiscardRequestValidator>();
            serviceCollection.AddTransient<IPageContentProvider, SiteBuilderContentProvider>();
            serviceCollection.AddTransient<IPagesMigrationService, PagesMigrationService>();
            serviceCollection.AddTransient<ISiteBuilderAssetReferenceMigrationService, SiteBuilderAssetReferenceMigrationService>();
            // The one-time startup migrations run as background jobs; their handlers call the concrete services.
            serviceCollection.AddTransient<PagesMigrationService>();
            serviceCollection.AddTransient<SiteBuilderAssetReferenceMigrationService>();
            serviceCollection.AddBackgroundJob<PagesMigrationJob, PagesMigrationJobPayload>(triggerable: false);
            serviceCollection.AddBackgroundJob<AssetReferenceIndexRebuildJob, AssetReferenceIndexRebuildJobPayload>(triggerable: false);
            serviceCollection.AddTransient<IPageBuilderSettingsMigrationService, PageBuilderSettingsMigrationService>();
            serviceCollection.AddTransient<PageBuilderSettingsMigrationService>();
            serviceCollection.AddBackgroundJob<PageBuilderSettingsCopyJob, PageBuilderSettingsCopyJobPayload>(triggerable: false);
            serviceCollection.AddTransient<SiteBuilderSharedComponentExportImport>();
            serviceCollection.AddTransient<MultilanguagePageExportImport>();
            serviceCollection.AddTransient<SiteBuilderExportImport>();

            _pageBuilderConfigurationKeys = AddSiteBuilderOptions(serviceCollection, Configuration);
            if (Configuration.GetConnectionString(ModuleInfo.Id) == null &&
                Configuration.GetConnectionString(SiteBuilderRepositoryRegistration.PageBuilderModuleId) != null)
            {
                _pageBuilderConfigurationKeys.Add(new(
                    $"ConnectionStrings:{SiteBuilderRepositoryRegistration.PageBuilderModuleId}",
                    $"ConnectionStrings:{ModuleInfo.Id}"));
            }

            serviceCollection.AddTransient<IGitHubConnectionService, GitHubConnectionService>();
            serviceCollection.AddTransient<IGitHubApiClient, GitHubApiClient>();
            serviceCollection.AddTransient<GitHubAuthHandler>();
            serviceCollection.AddHttpClient(GitHubApiClient.HttpClientName)
                .ConfigureHttpClient(ConfigureGitHubClient)
                .AddHttpMessageHandler<GitHubAuthHandler>();
            serviceCollection.AddHttpClient(GitHubApiClient.AnonymousHttpClientName).ConfigureHttpClient(ConfigureGitHubClient);
            serviceCollection.AddTransient<IGitContentRepository, GitHubContentRepository>();
            serviceCollection.AddTransient<IGitContentHistory, GitHubContentHistory>();
            serviceCollection.AddTransient<IGitContentPublisher, GitHubContentPublisher>();
            serviceCollection.AddTransient<IGitContentOwnership, GitHubContentOwnership>();
            serviceCollection.AddTransient<IGitContentPolicy, GitContentPolicy>();
            serviceCollection.AddTransient<IGitContentProvisioner, GitHubContentProvisioner>();
            serviceCollection.AddTransient<IGitContentSetupService, GitContentSetupService>();
            serviceCollection.AddTransient<IGitLivePageService, GitLivePageService>();
            serviceCollection.AddTransient<IContentHistory, GitHubSiteHistory>();

            serviceCollection.AddTransient<IThemeStorageFactory, ThemeStorageFactory>();
            serviceCollection.AddTransient<IThemeSnapshotBuilder, ThemeSnapshotBuilder>();
            serviceCollection.AddTransient<IThemeValidator, ThemeValidator>();
            serviceCollection.AddTransient<ISchemaCatalogService, SchemaCatalogService>();
            serviceCollection.AddTransient<IThemePublisher, ThemePublisher>();
            serviceCollection.AddTransient<IThemeDraftService, ThemeDraftService>();
            serviceCollection.AddTransient<IFilePageDraftService, FilePageDraftService>();
            serviceCollection.AddTransient<IDesignerContentService, DesignerContentService>();
            serviceCollection.AddTransient<IDesignerOperationService, DesignerOperationService>();
            serviceCollection.AddTransient<IThemeOperationService, ThemeOperationService>();
            serviceCollection.AddTransient<ISiteBuilderContentQueryService, SiteBuilderContentQueryService>();
            serviceCollection.AddTransient<ISiteBuilderPreviewQueryService, SiteBuilderPreviewQueryService>();
            serviceCollection.AddTransient<ISiteBuilderPublishStatusQueryService, SiteBuilderPublishStatusQueryService>();
            serviceCollection.AddTransient<IFilePageOperationService, FilePageOperationService>();
            serviceCollection.AddTransient<IContentHistoryOperationService, ContentHistoryOperationService>();
            serviceCollection.AddTransient<ISiteBuilderSharedComponentOperationService, SiteBuilderSharedComponentOperationService>();
            serviceCollection.AddTransient<IContentPermissionService, ContentPermissionService>();
            serviceCollection.AddTransient<IThemeSnapshotService, ThemeSnapshotService>();
            serviceCollection.AddTransient<IThemePaletteService, ThemePaletteService>();
            serviceCollection.AddTransient<IValidator<SiteBuilderStoreRequest>, SiteBuilderStoreRequestValidator>();
            serviceCollection.AddTransient<IValidator<ContentHistoryRequest>, ContentHistoryRequestValidator>();
            serviceCollection.AddTransient<IValidator<ContentHistoryRestoreRequest>, ContentHistoryRestoreRequestValidator>();
            serviceCollection.AddTransient<IValidator<GitHubAppSetupRequest>, GitHubAppSetupRequestValidator>();
            serviceCollection.AddTransient<IValidator<ThemePaletteTokens>, ThemePaletteTokensValidator>();
            serviceCollection.AddTransient<IValidator<ThemePaletteGenerationRequest>, ThemePaletteGenerationRequestValidator>();
            serviceCollection.AddTransient<IValidator<ThemePaletteCheckRequest>, ThemePaletteCheckRequestValidator>();
            serviceCollection.AddTransient<GitContentStoreChangedEventHandler>();
            serviceCollection.AddBackgroundJob<GitContentRepositoryJob, GitContentRepositoryJobPayload>(triggerable: false);

            var isFullTextSearchEnabled = Configuration.IsContentFullTextSearchEnabled();

            if (isFullTextSearchEnabled)
            {
                serviceCollection.AddTransient<SiteBuilderContentItemBuilder>();
            }

            _ = new GraphQLBuilder(serviceCollection, builder =>
            {
                builder.AddSchema(serviceCollection, typeof(XapiAssemblyMarker));
            });

            serviceCollection.AddSingleton<ScopedSchemaFactory<XapiAssemblyMarker>>();
            serviceCollection.AddSiteBuilderPreview();

            if (ModuleService.IsInstalled(McpModuleId))
            {
                // The Site Builder toolset, in the MCP module's admin scope.
                serviceCollection.AddMcpCapabilities<McpAssemblyMarker>();
            }
        }

        internal static List<RenamedConfigurationKey> AddSiteBuilderOptions(IServiceCollection serviceCollection, IConfiguration configuration)
        {
            var pageBuilderKeys = new List<RenamedConfigurationKey>();

            var gitContentSection = GetSection(GitContentOptions.SectionName);
            serviceCollection.Configure<GitContentOptions>(gitContentSection);
            // Fail fast: a half-configured flow must not boot, or publishing would silently keep writing
            // straight to production on an installation whose operator expects pull requests.
            gitContentSection.Get<GitContentOptions>()?.Validate();

            serviceCollection.Configure<ThemeSnapshotOptions>(GetSection(ThemeSnapshotOptions.SectionName));

            return pageBuilderKeys;

            IConfigurationSection GetSection(string sectionName)
            {
                var section = configuration.GetSiteBuilderSection(sectionName, out var isPageBuilderSection);
                if (isPageBuilderSection)
                {
                    pageBuilderKeys.Add(new(section.Path, sectionName));
                }

                return section;
            }
        }

        internal static void WarnAboutPageBuilderConfiguration(ILogger logger, IList<RenamedConfigurationKey> pageBuilderKeys)
        {
            if (pageBuilderKeys.Count > 0)
            {
                logger.LogWarning("Site Builder read Page Builder's configuration, whose names are deprecated: {Keys}. Rename them; the old names will stop working in a future version.",
                    string.Join(", ", pageBuilderKeys.Select(x => $"{x.PageBuilderKey} -> {x.SiteBuilderKey}")));
            }
        }

        internal sealed record RenamedConfigurationKey(string PageBuilderKey, string SiteBuilderKey);

        private static void ConfigureGitHubClient(IServiceProvider provider, HttpClient httpClient)
        {
            GitHubApiClient.ConfigureHttpClient(httpClient, provider.GetRequiredService<IOptions<GitContentOptions>>().Value);
        }

        public void PostInitialize(IApplicationBuilder appBuilder)
        {
            _appBuilder = appBuilder;
            var serviceProvider = appBuilder.ApplicationServices;

            WarnAboutPageBuilderConfiguration(serviceProvider.GetRequiredService<ILogger<Module>>(), _pageBuilderConfigurationKeys);

            var settingsRegistrar = serviceProvider.GetRequiredService<ISettingsRegistrar>();
            settingsRegistrar.RegisterSettings(ModuleConstants.Settings.AllSettings, ModuleInfo.Id);
            settingsRegistrar.RegisterSettingsForType(ModuleConstants.Settings.StoreLevelSettings.AllStoreLevelSettings, ModuleConstants.Settings.StoreSettingsObjectType);

            var permissionsProvider = serviceProvider.GetRequiredService<IPermissionsRegistrar>();
            permissionsProvider.RegisterPermissions(ModuleInfo.Id, "Site builder", ModuleConstants.Security.Permissions.AllPermissions);
            // the read permissions decide which stores a role works in; each action checks them with the store
            permissionsProvider.WithAvailabeScopesForPermissions(
                [ModuleConstants.Security.Permissions.Read, ModuleConstants.Security.Permissions.SharedComponentsRead],
                new StoreSelectedScope());

            var isFullTextSearchEnabled = Configuration.IsContentFullTextSearchEnabled();

            if (isFullTextSearchEnabled)
            {
                var contentItemTypeRegistrar = serviceProvider.GetService<IContentItemTypeRegistrar>();
                contentItemTypeRegistrar.RegisterContentItemType(".page", serviceProvider.GetService<SiteBuilderContentItemBuilder>);
            }

            appBuilder.RegisterEventHandler<SiteBuilderPageChangedEvent, SiteBuilderPageChangedEventHandler>();
            appBuilder.RegisterEventHandler<GroupedSiteBuilderPageChangedEvent, GroupedSiteBuilderPageChangedEventHandler>();
            appBuilder.RegisterEventHandler<SiteBuilderSharedComponentContentChangedEvent, SiteBuilderSharedComponentContentChangedEventHandler>();
            appBuilder.RegisterEventHandler<StoreChangedEvent, GitContentStoreChangedEventHandler>();

            appBuilder.UseScopedSchema<XapiAssemblyMarker>("site-builder");

            // Apply migrations
            using var serviceScope = serviceProvider.CreateScope();
            serviceScope.ServiceProvider.MigrateSiteBuilderDatabase();

            // Run pages migration
            var pagesMigrationService = serviceScope.ServiceProvider.GetRequiredService<IPagesMigrationService>();
            pagesMigrationService.StartMigration();

            // Build asset reference index for existing pages
            var assetReferenceMigrationService = serviceScope.ServiceProvider.GetRequiredService<ISiteBuilderAssetReferenceMigrationService>();
            assetReferenceMigrationService.StartMigration();

            var pageBuilderSettingsMigrationService = serviceScope.ServiceProvider.GetRequiredService<IPageBuilderSettingsMigrationService>();
            pageBuilderSettingsMigrationService.StartMigration();

            // site-builder
            var siteBuilderAppPath = Path.Combine(ModuleInfo.FullPhysicalPath, "site-builder", "dist");
            if (Directory.Exists(siteBuilderAppPath))
            {
                appBuilder.UseDefaultFiles(new DefaultFilesOptions
                {
                    FileProvider = new PhysicalFileProvider(siteBuilderAppPath),
                    RequestPath = new PathString($"/apps/site-builder")
                });
                appBuilder.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(siteBuilderAppPath),
                    RequestPath = new PathString($"/apps/site-builder")
                });
            }
        }

        public void Uninstall()
        {
            // Method intentionally left empty.
        }

        public Task ExportAsync(Stream outStream, ExportImportOptions options, Action<ExportImportProgressInfo> progressCallback,
            CancellationToken cancellationToken)
        {
            return _appBuilder.ApplicationServices.GetRequiredService<SiteBuilderExportImport>().DoExportAsync(outStream,
                progressCallback, cancellationToken);
        }

        public Task ImportAsync(Stream inputStream, ExportImportOptions options, Action<ExportImportProgressInfo> progressCallback,
            CancellationToken cancellationToken)
        {
            return _appBuilder.ApplicationServices.GetRequiredService<SiteBuilderExportImport>().DoImportAsync(inputStream,
                progressCallback, cancellationToken);
        }
    }
}
