using System;
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
using VirtoCommerce.Platform.Core.Events;
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

    private sealed class NoSchemaCatalogs : ISchemaCatalogService
    {
        public Task<string> SaveAsync(string storeId, string catalogJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> GetAsync(string storeId, string hash, CancellationToken cancellationToken = default) => Task.FromResult<string>(null);
    }
}
