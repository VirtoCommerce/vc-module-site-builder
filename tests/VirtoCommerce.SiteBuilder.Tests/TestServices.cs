using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.DistributedLock;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using VirtoCommerce.StoreModule.Core.Services;
using Fakes = VirtoCommerce.SiteBuilder.Tests.PublishedRenameContentPreservationTests;

namespace VirtoCommerce.SiteBuilder.Tests;

internal static class TestServices
{
    public static SiteBuilderContentGuard ContentGuard { get; } = new(new SiteBuilderHtmlSanitizer());

    public static ContentPermissionService Permissions(IAuthorizationService authorizationService) => new(authorizationService);

    public static FilePageDraftService FilePageDrafts(
        IOptions<GitContentOptions> options,
        IGitHubConnectionService connection,
        IGitContentPolicy policy,
        IGitContentRepository repository = null,
        IGitContentHistory history = null,
        IGitContentProvisioner provisioner = null,
        IStoreService storeService = null,
        IContentPathResolver pathResolver = null,
        IBlobContentStorageProviderFactory blobs = null,
        IPublishingService publishing = null,
        IEventPublisher events = null) =>
        new(policy, repository, history, provisioner ?? new FakeGitContentProvisioner(), connection, options,
            new ThemeStorageFactory(storeService, pathResolver, blobs, publishing), pathResolver, blobs, publishing, events);

    public static FilePageDraftService FilePageDrafts(FakeBlobStorage live, IOptions<GitContentOptions> options, IGitHubConnectionService connection,
        IGitContentPolicy policy, IGitContentRepository repository = null, IGitContentHistory history = null, IGitContentProvisioner provisioner = null) =>
        FilePageDrafts(options, connection, policy, repository, history, provisioner, live?.StoreService, live, live, live, live);

    public static DesignerContentService Designer(
        IContentHistory contentHistory,
        IFilePageDraftService filePageDrafts = null,
        IThemeDraftService themeDrafts = null,
        Fakes.FakeGroupedPageService pages = null,
        IEventPublisher events = null,
        IThemePublisher themePublisher = null,
        IThemeSnapshotService themeSnapshots = null,
        ISchemaCatalogService catalogs = null)
    {
        var contentGuard = ContentGuard;
        var contentWriter = pages == null
            ? null
            : new GroupedPageContentWriter(pages, new Fakes.FakeSiteBuilderPageService(pages), events ?? new Fakes.NoopEventPublisher(), contentGuard);

        return new DesignerContentService(themeDrafts, themePublisher, themeSnapshots, pages == null ? null : new Fakes.FakeSiteBuilderPageService(pages),
            pages, pages, contentWriter, filePageDrafts, contentHistory, contentGuard, catalogs ?? new NoSchemaCatalogs());
    }

    public static DesignerOperationService DesignerOperations(
        IAuthorizationService authorizationService,
        IContentPermissionService contentPermissionService,
        IValidator<SiteBuilderContentRequest> requestValidator,
        IDesignerContentService designerContentService,
        IThemeDraftService themeDraftService = null,
        IGroupedPageService groupedPageService = null,
        ISiteBuilderSharedComponentPlacementValidator sharedComponentPlacementValidator = null) =>
        new(authorizationService, contentPermissionService, requestValidator, RequestValidators.DesignerSave, RequestValidators.DesignerDiscard,
            groupedPageService, sharedComponentPlacementValidator, designerContentService, themeDraftService);

    public static GroupedPageOperationService GroupedPageOperations(
        IAuthorizationService authorizationService,
        IGroupedPageService groupedPageService,
        SiteBuilderPageContentService pageContentService = null,
        ISiteBuilderPageService pageService = null,
        ISiteBuilderSharedComponentPlacementValidator sharedComponentPlacementValidator = null,
        IDesignerContentService designerContentService = null,
        IContentHistory contentHistory = null) =>
        new(authorizationService, groupedPageService, pageService, pageContentService,
            sharedComponentPlacementValidator ?? new SiteBuilderSharedComponentPlacementValidator(new NoopSharedComponentReferenceIndexService(), authorizationService),
            designerContentService ?? Designer(contentHistory ?? new NoContentHistory()), ContentGuard, contentHistory ?? new NoContentHistory());

    public static ThemeOperationService ThemeOperations(IAuthorizationService authorizationService, IThemeSnapshotService themeSnapshots, IThemePublisher themePublisher,
        IContentHistory contentHistory = null, string storeId = "s1") =>
        new(authorizationService, RequestValidators.Stores(storeId), themeSnapshots, themePublisher, contentHistory ?? new NoContentHistory());

    public static ContentHistoryOperationService ContentHistoryOperations(IAuthorizationService authorizationService, IContentHistory contentHistory,
        IGroupedPageService groupedPageService, IDesignerContentService designerContentService, string storeId = "s1") =>
        new(authorizationService, contentHistory, groupedPageService, designerContentService, Permissions(authorizationService),
            new SiteBuilderSharedComponentPlacementValidator(new NoopSharedComponentReferenceIndexService(), authorizationService),
            RequestValidators.Stores(storeId), RequestValidators.History(storeId), RequestValidators.Restore(storeId));

    public static SiteBuilderSharedComponentOperationService SharedComponentOperations(IAuthorizationService authorizationService,
        ISiteBuilderSharedComponentService sharedComponentService, ISiteBuilderSharedComponentContentService sharedComponentContentService = null,
        ISiteBuilderSharedComponentUsageService sharedComponentUsageService = null, IStoreService storeService = null,
        ISiteBuilderSharedComponentSearchService sharedComponentSearchService = null) =>
        new(sharedComponentService, sharedComponentSearchService, sharedComponentContentService, sharedComponentUsageService, authorizationService, storeService,
            ContentGuard);

    public static SiteBuilderPublishStatusQueryService PublishStatuses(IAuthorizationService authorizationService, IGroupedPageService groupedPageService) =>
        new(authorizationService, null, groupedPageService, null, null, null, null, null, null, null);

    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "One for each service of the operations.")]
    public static FilePageOperationService FilePageOperations(
        IAuthorizationService authorizationService,
        IValidator<SiteBuilderContentRequest> requestValidator,
        IOptions<GitContentOptions> gitContentOptions,
        IGitHubConnectionService gitHubConnectionService,
        IGitContentPolicy gitContentPolicy,
        IGitContentRepository gitContentRepository,
        IGitContentHistory gitContentHistory = null,
        IGitContentPublisher gitContentPublisher = null,
        IGitLivePageService gitLivePageService = null,
        IFilePageDraftService filePageDraftService = null,
        IPublishingService publishingService = null,
        IThemeDraftService themeDraftService = null,
        IContentHistory contentHistory = null,
        IContentPermissionService contentPermissionService = null,
        ISiteBuilderContentGuard contentGuard = null) =>
        new(authorizationService, requestValidator, contentPermissionService ?? Permissions(authorizationService), publishingService, gitContentPolicy,
            gitContentRepository, gitContentHistory, gitContentPublisher, gitLivePageService, gitHubConnectionService, gitContentOptions, filePageDraftService,
            themeDraftService, contentHistory ?? new NoContentHistory(), contentGuard ?? ContentGuard);

    /// <summary>The controller with its query and operation services built from the same fakes, as the platform's container builds them.</summary>
    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "One for each service of the controller.")]
    public static SiteBuilderController CreateSiteBuilderController(
        IStoreService storeService,
        IContentPathResolver pathResolver,
        IBlobContentStorageProviderFactory blobContentStorageProviderFactory,
        IPublishingService publishingService,
        IEventPublisher eventPublisher,
        IOptions<GitContentOptions> gitContentOptions,
        IGitHubConnectionService gitHubConnectionService,
        IGitContentPolicy gitContentPolicy,
        IGitContentRepository gitContentRepository,
        IGitContentHistory gitContentHistory,
        IGitContentPublisher gitContentPublisher,
        IGitLivePageService gitLivePageService,
        ISettingsManager settingsManager,
        IAuthorizationService authorizationService,
        IDistributedLock distributedLock,
        IValidator<SiteBuilderContentRequest> requestValidator,
        IContentHistory contentHistory,
        IThemeDraftService themeDraftService,
        IDesignerOperationService designerOperationService,
        IContentPermissionService contentPermissionService,
        IFilePageDraftService filePageDraftService,
        IThemeStorageFactory themeStorageFactory,
        ISiteBuilderContentGuard contentGuard,
        IGroupedPageService groupedPageService = null)
    {
        var contentQueries = new SiteBuilderContentQueryService(authorizationService, requestValidator, pathResolver, blobContentStorageProviderFactory,
            publishingService, themeStorageFactory, gitContentPolicy, gitContentRepository, gitHubConnectionService, gitContentOptions, gitContentHistory);
        var previews = new SiteBuilderPreviewQueryService(authorizationService, requestValidator, storeService, settingsManager, groupedPageService,
            contentQueries, gitContentPolicy, gitContentRepository, gitHubConnectionService, gitContentOptions);
        var publishStatuses = new SiteBuilderPublishStatusQueryService(authorizationService, requestValidator, groupedPageService, publishingService,
            gitContentPolicy, gitContentRepository, gitContentPublisher, gitLivePageService, gitHubConnectionService, gitContentOptions);

        var filePageOperations = FilePageOperations(authorizationService, requestValidator, gitContentOptions, gitHubConnectionService, gitContentPolicy,
            gitContentRepository, gitContentHistory, gitContentPublisher, gitLivePageService, filePageDraftService, publishingService, themeDraftService,
            contentHistory, contentPermissionService, contentGuard);

        return new SiteBuilderController(pathResolver, blobContentStorageProviderFactory, publishingService, eventPublisher, gitContentOptions,
            gitHubConnectionService, gitContentPolicy, gitContentRepository, authorizationService, distributedLock, requestValidator,
            contentHistory, designerOperationService, contentPermissionService, filePageDraftService, themeStorageFactory, contentGuard, contentQueries,
            previews, publishStatuses, filePageOperations);
    }

    private sealed class NoSchemaCatalogs : ISchemaCatalogService
    {
        public Task<string> SaveAsync(string storeId, string catalogJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> GetAsync(string storeId, string hash, CancellationToken cancellationToken = default) => Task.FromResult<string>(null);
    }
}
