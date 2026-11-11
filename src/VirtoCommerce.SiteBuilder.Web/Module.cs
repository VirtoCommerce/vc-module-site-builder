using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using VirtoCommerce.ContentModule.Core.Extensions;
using VirtoCommerce.ContentModule.Core.Search;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Data.BackgroundJobs;
using VirtoCommerce.SiteBuilder.Data.ContentProviders;
using VirtoCommerce.SiteBuilder.Data.ExportImport;
using VirtoCommerce.SiteBuilder.Data.GitContent;
using VirtoCommerce.SiteBuilder.Data.Handlers;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.SiteBuilder.Data.Search;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Web.Services;
using VirtoCommerce.Pages.Core.ContentProviders;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.ExportImport;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Platform.Data.MySql.Extensions;
using VirtoCommerce.Platform.Data.PostgreSql.Extensions;
using VirtoCommerce.Platform.Data.SqlServer.Extensions;
using VirtoCommerce.StoreModule.Core.Model;

namespace VirtoCommerce.SiteBuilder.Web
{
    public class Module : IModule, IExportSupport, IImportSupport, IHasConfiguration
    {
        private IApplicationBuilder _appBuilder;

        public ManifestModuleInfo ModuleInfo { get; set; }
        public IConfiguration Configuration { get; set; }

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
            serviceCollection.AddTransient<ISiteBuilderSharedComponentUsageService, SiteBuilderSharedComponentUsageService>();
            serviceCollection.AddTransient<
                ISiteBuilderSharedComponentAssetReferenceIndexService,
                SiteBuilderSharedComponentAssetReferenceIndexService>();
            serviceCollection.AddTransient<SiteBuilderPageContentService>();

            serviceCollection.AddTransient<GroupedSiteBuilderPageChangedEventHandler>();
            serviceCollection.AddTransient<SiteBuilderSharedComponentContentChangedEventHandler>();
            serviceCollection.AddBackgroundJob<SiteBuilderSharedComponentContentPropagationJob, SiteBuilderSharedComponentContentPropagationJobPayload>(triggerable: false);

            serviceCollection.AddTransient<IAuthorizationHandler, SiteBuilderAuthorizationHandler>();
            serviceCollection.AddTransient<IPageContentProvider, SiteBuilderContentProvider>();
            serviceCollection.AddTransient<IPagesMigrationService, PagesMigrationService>();
            serviceCollection.AddTransient<ISiteBuilderAssetReferenceMigrationService, SiteBuilderAssetReferenceMigrationService>();
            // The one-time startup migrations run as background jobs; their handlers call the concrete services.
            serviceCollection.AddTransient<PagesMigrationService>();
            serviceCollection.AddTransient<SiteBuilderAssetReferenceMigrationService>();
            serviceCollection.AddBackgroundJob<PagesMigrationJob, PagesMigrationJobPayload>(triggerable: false);
            serviceCollection.AddBackgroundJob<AssetReferenceIndexRebuildJob, AssetReferenceIndexRebuildJobPayload>(triggerable: false);
            serviceCollection.AddTransient<SiteBuilderSharedComponentExportImport>();
            serviceCollection.AddTransient<MultilanguagePageExportImport>();
            serviceCollection.AddTransient<SiteBuilderExportImport>();

            // Git content flow (disabled until SiteBuilder:GitContent:Enabled is set AND a store opts in
            // — see IGitContentPolicy, GitContentOptions and vc-content/.github/git-flow-setup.md).
            var gitContentSection = Configuration.GetSection(GitContentOptions.SectionName);
            serviceCollection.Configure<GitContentOptions>(gitContentSection);
            // Fail fast: a half-configured flow must not boot, or publishing would silently keep writing
            // straight to production on an installation whose operator expects pull requests.
            gitContentSection.Get<GitContentOptions>()?.Validate();
            serviceCollection.AddHttpClient(GitHubContentRepository.HttpClientName)
                .ConfigureHttpClient((provider, httpClient) =>
                {
                    var gitContentOptions = provider.GetRequiredService<IOptions<GitContentOptions>>().Value;
                    // Relative request uris are combined against BaseAddress, so it must end with '/'.
                    httpClient.BaseAddress = new Uri(gitContentOptions.ApiUrl.AbsoluteUri.TrimEnd('/') + "/");
                    httpClient.Timeout = gitContentOptions.RequestTimeout;
                    httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                    httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                    // GitHub rejects requests without a User-Agent.
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "vc-sitebuilder-module");
                    if (!string.IsNullOrEmpty(gitContentOptions.Token))
                    {
                        httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + gitContentOptions.Token);
                    }
                });
            serviceCollection.AddTransient<IGitContentRepository, GitHubContentRepository>();
            serviceCollection.AddTransient<IGitContentHistory, GitHubContentHistory>();
            serviceCollection.AddTransient<IGitContentPublisher, GitHubContentPublisher>();
            serviceCollection.AddTransient<IGitContentPolicy, GitContentPolicy>();

            var isFullTextSearchEnabled = Configuration.IsContentFullTextSearchEnabled();

            if (isFullTextSearchEnabled)
            {
                serviceCollection.AddTransient<SiteBuilderContentItemBuilder>();
            }

        }

        public void PostInitialize(IApplicationBuilder appBuilder)
        {
            _appBuilder = appBuilder;
            var serviceProvider = appBuilder.ApplicationServices;

            var settingsRegistrar = serviceProvider.GetRequiredService<ISettingsRegistrar>();
            settingsRegistrar.RegisterSettings(ModuleConstants.Settings.AllSettings, ModuleInfo.Id);
            settingsRegistrar.RegisterSettingsForType(ModuleConstants.Settings.StoreLevelSettings.AllStoreLevelSettings, ModuleConstants.Settings.StoreSettingsObjectType);

            var permissionsProvider = serviceProvider.GetRequiredService<IPermissionsRegistrar>();
            permissionsProvider.RegisterPermissions(ModuleInfo.Id, "Site builder", ModuleConstants.Security.Permissions.AllPermissions);

            var isFullTextSearchEnabled = Configuration.IsContentFullTextSearchEnabled();

            if (isFullTextSearchEnabled)
            {
                var contentItemTypeRegistrar = serviceProvider.GetService<IContentItemTypeRegistrar>();
                contentItemTypeRegistrar.RegisterContentItemType(".page", serviceProvider.GetService<SiteBuilderContentItemBuilder>);
            }

            appBuilder.RegisterEventHandler<SiteBuilderPageChangedEvent, SiteBuilderPageChangedEventHandler>();
            appBuilder.RegisterEventHandler<GroupedSiteBuilderPageChangedEvent, GroupedSiteBuilderPageChangedEventHandler>();
            appBuilder.RegisterEventHandler<SiteBuilderSharedComponentContentChangedEvent, SiteBuilderSharedComponentContentChangedEventHandler>();

            // Apply migrations
            using var serviceScope = serviceProvider.CreateScope();
            serviceScope.ServiceProvider.MigrateSiteBuilderDatabase();

            // Run pages migration
            var pagesMigrationService = serviceScope.ServiceProvider.GetRequiredService<IPagesMigrationService>();
            pagesMigrationService.StartMigration();

            // Build asset reference index for existing pages
            var assetReferenceMigrationService = serviceScope.ServiceProvider.GetRequiredService<ISiteBuilderAssetReferenceMigrationService>();
            assetReferenceMigrationService.StartMigration();

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
