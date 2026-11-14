using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using VirtoCommerce.ContentModule.Core.Model;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using VirtoCommerce.StoreModule.Core.Model;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

/// <summary>
/// The designer and the admin page blade publish through these actions on either flow, so the builder's
/// permission and store checks guard a blob publish too.
/// </summary>
public class BlobPublishTests
{
    private const string Page = "about-us.page";

    [Fact]
    public async Task Publish_PublishesTheBlob()
    {
        var publishing = new RecordingPublishingService();

        var result = Assert.IsType<OkObjectResult>(await Controller(publishing).GitPublish("vccom", Page, "pages"));

        Assert.Equal(("pages", "vccom", Page, true), Assert.Single(publishing.Calls));
        Assert.Equal(nameof(GitPublishState.Merged), JObject.FromObject(result.Value).Value<string>("state"));
    }

    [Fact]
    public async Task Unpublish_UnpublishesTheBlob()
    {
        var publishing = new RecordingPublishingService();

        Assert.IsType<OkResult>(await Controller(publishing).GitUnpublish("vccom", Page, "blogs"));

        Assert.Equal(("blogs", "vccom", Page, false), Assert.Single(publishing.Calls));
    }

    [Fact]
    public async Task PublishStatus_ReportsTheBlobStatus()
    {
        var publishing = new RecordingPublishingService { Status = new FilePublishStatus { Published = true, HasChanges = true } };

        var result = await Controller(publishing).GitPublishStatus("vccom", Page, "pages");

        Assert.IsType<OkObjectResult>(result.Result);
        var status = PlatformJson.ParseBody(result);
        Assert.True(status.Value<bool>("published"));
        Assert.True(status.Value<bool>("hasChanges"));
        Assert.Equal("blob", status.Value<string>("flow"));
    }

    [Fact]
    public async Task GitPublishStatus_BlobFlow_WritesTheStatusJson()
    {
        var publishing = new RecordingPublishingService { Status = new FilePublishStatus { Published = true, HasChanges = true } };

        var result = await Controller(publishing).GitPublishStatus("vccom", Page, "pages");

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("""{"published":true,"hasChanges":true,"pending":false,"awaitingMerge":false,"flow":"blob","liveBehind":false}""", PlatformJson.SerializeBody(result));
    }

    [Fact]
    public async Task Publish_ForAStoreTheUserCannotReach_PublishesNothing()
    {
        var publishing = new RecordingPublishingService();

        Assert.IsType<ForbidResult>(await Controller(publishing, allowed: false).GitPublish("vccom", Page, "pages"));

        Assert.Empty(publishing.Calls);
    }

    [Fact]
    public async Task Unpublish_ForAStoreTheUserCannotReach_UnpublishesNothing()
    {
        var publishing = new RecordingPublishingService();

        Assert.IsType<ForbidResult>(await Controller(publishing, allowed: false).GitUnpublish("vccom", Page, "pages"));

        Assert.Empty(publishing.Calls);
    }

    [Theory]
    [InlineData("../about-us.page")]
    [InlineData("about-us.json")]
    public async Task Publish_APathThatIsNotAPage_PublishesNothing(string path)
    {
        var publishing = new RecordingPublishingService();

        Assert.IsType<BadRequestObjectResult>(await Controller(publishing).GitPublish("vccom", path, "pages"));

        Assert.Empty(publishing.Calls);
    }

    [Fact]
    public async Task PublishOperation_DryRun_TellsTheVersionOfTheDraft()
    {
        var live = Live(draft: "{\"v\":2}");
        var request = new FilePagePublishOperationRequest { StoreId = "s1", ContentType = "pages", Path = Page, DryRun = true };

        var result = await Operations(new RecordingPublishingService(), live).PublishAsync(request, User, Ct);

        Assert.Equal(SiteBuilderContentVersion.CreateForFileContent("{\"v\":2}"), Assert.Single(result.Preview.Objects).ETag);
    }

    [Fact]
    public async Task PublishOperation_TheReviewedDraft_IsPublished()
    {
        var publishing = new RecordingPublishingService();
        var request = new FilePagePublishOperationRequest
        {
            StoreId = "s1", ContentType = "pages", Path = Page, ExpectedContentVersion = SiteBuilderContentVersion.CreateForFileContent("{\"v\":2}"),
        };

        var result = await Operations(publishing, Live(draft: "{\"v\":2}")).PublishAsync(request, User, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(("pages", "s1", Page, true), Assert.Single(publishing.Calls));
    }

    [Fact]
    public async Task PublishOperation_DraftSavedSinceTheReview_IsAConflictAndPublishesNothing()
    {
        var publishing = new RecordingPublishingService();
        var live = Live(draft: "{\"v\":2}");
        var operations = Operations(publishing, live);
        var reviewed = (await operations.PublishAsync(new FilePagePublishOperationRequest
        {
            StoreId = "s1", ContentType = "pages", Path = Page, DryRun = true,
        }, User, Ct)).Preview.Objects[0].ETag;
        live.Files[$"pages/s1/{Page}-draft"] = "{\"v\":3}";
        var request = new FilePagePublishOperationRequest { StoreId = "s1", ContentType = "pages", Path = Page, ExpectedContentVersion = reviewed };

        var result = await operations.PublishAsync(request, User, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Conflict, SiteBuilderContentConflictException.PageChangedMessage), (result.Status, result.Error));
        Assert.Empty(publishing.Calls);
    }

    [Fact]
    public async Task UnpublishOperation_PublishedPageChangedSinceTheReview_IsAConflictAndUnpublishesNothing()
    {
        var publishing = new RecordingPublishingService();
        var live = Live(draft: null);
        var reviewed = SiteBuilderContentVersion.CreateForFileContent(live.Files[$"pages/s1/{Page}"]);
        live.Files[$"pages/s1/{Page}"] = "{\"v\":9}";
        var request = new FilePageShipOperationRequest { StoreId = "s1", ContentType = "pages", Path = Page, ExpectedContentVersion = reviewed };

        var result = await Operations(publishing, live).UnpublishAsync(request, User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.Empty(publishing.Calls);
    }

    [Fact]
    public async Task UnpublishOperation_ThePublishedPageReviewed_IsUnpublished_WhateverTheDraftHolds()
    {
        var publishing = new RecordingPublishingService();
        var live = Live(draft: "{\"v\":2}");
        var request = new FilePageShipOperationRequest
        {
            StoreId = "s1", ContentType = "pages", Path = Page, ExpectedContentVersion = SiteBuilderContentVersion.CreateForFileContent(live.Files[$"pages/s1/{Page}"]),
        };

        var result = await Operations(publishing, live).UnpublishAsync(request, User, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(("pages", "s1", Page, false), Assert.Single(publishing.Calls));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "john")], "test"));

    private static FakeBlobStorage Live(string draft)
    {
        var live = new FakeBlobStorage();
        live.Files[$"pages/s1/{Page}"] = "{\"v\":1}";
        if (draft != null)
        {
            live.Files[$"pages/s1/{Page}-draft"] = draft;
        }

        return live;
    }

    private static FilePageOperationService Operations(RecordingPublishingService publishing, FakeBlobStorage live)
    {
        var options = Options.Create(new GitContentOptions());
        var connection = GitHubConnections.CreateService(options.Value);
        var policy = new FakeGitContentPolicy(enabled: false);
        var drafts = TestServices.FilePageDrafts(options, connection, policy, null, null, new FakeGitContentProvisioner(), live.StoreService, live, live, publishing, live);
        var validator = new SiteBuilderContentRequestValidator(new SiteBuilderStoreRequestValidator(live.StoreService), new SiteBuilderContentFileValidator());

        return TestServices.FilePageOperations(new FakeAuthorizationService(true), validator, options, connection, policy, gitContentRepository: null,
            filePageDraftService: drafts, publishingService: publishing);
    }

    private static SiteBuilderController Controller(RecordingPublishingService publishing, bool allowed = true)
    {
        // The git dependencies are left null on purpose: a store off the git flow must not reach them.
        var filePageDrafts = TestServices.FilePageDrafts(Options.Create(new GitContentOptions()), GitHubConnections.CreateService(Options.Create(new GitContentOptions()).Value), new FakeGitContentPolicy(enabled: false), null, null, new FakeGitContentProvisioner(), new FakeStoreService(new Store { Id = "vccom" }), null, null, publishing, null);
        var controller = TestServices.CreateSiteBuilderController(
            storeService: new FakeStoreService(new Store { Id = "vccom" }),
            pathResolver: null,
            blobContentStorageProviderFactory: null,
            publishingService: publishing,
            eventPublisher: null,
            gitContentOptions: Options.Create(new GitContentOptions()),
            gitHubConnectionService: GitHubConnections.CreateService(Options.Create(new GitContentOptions()).Value),
            gitContentPolicy: new FakeGitContentPolicy(enabled: false),
            gitContentRepository: null,
            gitContentHistory: null,
            gitContentPublisher: null,
            gitLivePageService: null,
            settingsManager: null,
            authorizationService: new FakeAuthorizationService(allowed),
            distributedLock: new InProcessDistributedLock(),
            requestValidator: new SiteBuilderContentRequestValidator(new SiteBuilderStoreRequestValidator(new FakeStoreService(new Store { Id = "vccom" })), new SiteBuilderContentFileValidator()),
            contentHistory: new NoContentHistory(),
            themeDraftService: null,
            designerOperationService: TestServices.DesignerOperations(
                authorizationService: new FakeAuthorizationService(allowed),
                contentPermissionService: TestServices.Permissions(new FakeAuthorizationService(allowed)),
                requestValidator: new SiteBuilderContentRequestValidator(new SiteBuilderStoreRequestValidator(new FakeStoreService(new Store { Id = "vccom" })), new SiteBuilderContentFileValidator()),
                designerContentService: TestServices.Designer(new NoContentHistory(), filePageDrafts, null, null, null, null, null)),
            contentPermissionService: TestServices.Permissions(new FakeAuthorizationService(allowed)),
            filePageDraftService: filePageDrafts,
            contentGuard: TestServices.ContentGuard,
            themeStorageFactory: new ThemeStorageFactory(new FakeStoreService(new Store { Id = "vccom" }), null, null, publishing));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "john")], "test")) },
        };

        return controller;
    }

    private sealed class RecordingPublishingService : IPublishingService
    {
        public List<(string ContentType, string StoreId, string RelativeUrl, bool Publish)> Calls { get; } = [];

        public FilePublishStatus Status { get; init; } = new();

        public Task PublishingAsync(string contentType, string storeId, string relativeUrl, bool publish)
        {
            Calls.Add((contentType, storeId, relativeUrl, publish));
            return Task.CompletedTask;
        }

        public Task<FilePublishStatus> PublishStatusAsync(string contentType, string storeId, string relativeUrl) =>
            Task.FromResult(Status);

        public Task<IEnumerable<ContentFile>> SetFilesStatuses(IEnumerable<ContentFile> files) => Task.FromResult(files);

        public string GetRelativeDraftUrl(string source, bool draft) => draft ? source + "-draft" : source;

        public bool IsDraft(string relativeUrl) => relativeUrl.EndsWith("-draft");
    }
}
