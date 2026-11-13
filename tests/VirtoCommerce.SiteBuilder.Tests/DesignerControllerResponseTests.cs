using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

/// <summary>The designer endpoints answer each outcome of an operation with the response they always gave it.</summary>
public class DesignerControllerResponseTests
{
    private readonly StubDesignerOperations _operations = new();

    [Fact]
    public async Task SaveDesignerChanges_Saved_AnswersTheResult()
    {
        var saved = new DesignerSaveResult { SaveId = "save-0001", ETag = "\"p1\"" };
        _operations.Result = SiteBuilderOperationResult.Success(saved);

        var result = await Controller().SaveDesignerChanges("s1", new DesignerSaveRequest());

        Assert.Same(saved, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task SaveDesignerChanges_Forbidden_AnswersForbid()
    {
        _operations.Result = SiteBuilderOperationResult.Forbidden();

        Assert.IsType<ForbidResult>((await Controller().SaveDesignerChanges("s1", new DesignerSaveRequest())).Result);
    }

    [Fact]
    public async Task SaveDesignerChanges_NotFound_AnswersNotFoundWithoutBody()
    {
        _operations.Result = SiteBuilderOperationResult.NotFound();

        Assert.IsType<NotFoundResult>((await Controller().SaveDesignerChanges("s1", new DesignerSaveRequest())).Result);
    }

    [Fact]
    public async Task SaveDesignerChanges_Invalid_AnswersTheErrorAlone()
    {
        _operations.Result = SiteBuilderOperationResult.Invalid("Name the page or a theme file.");

        var result = await Controller().SaveDesignerChanges("s1", new DesignerSaveRequest());

        Assert.Equal("""{"error":"Name the page or a theme file."}""", PlatformJson.SerializeBody(Assert.IsType<BadRequestObjectResult>(result.Result)));
    }

    [Fact]
    public async Task SaveDesignerChanges_InvalidWithEachError_AnswersThemAll()
    {
        _operations.Result = SiteBuilderOperationResult.Invalid("The theme is not valid: a; b", ["a", "b"]);

        var result = await Controller().SaveDesignerChanges("s1", new DesignerSaveRequest());

        Assert.Equal("""{"error":"The theme is not valid: a; b","errors":["a","b"]}""",
            PlatformJson.SerializeBody(Assert.IsType<BadRequestObjectResult>(result.Result)));
    }

    [Fact]
    public async Task SaveDesignerChanges_Conflict_AnswersPreconditionFailedWithTheMessage()
    {
        _operations.Result = SiteBuilderOperationResult.Conflict("Saved by someone else.");

        var result = Assert.IsType<ObjectResult>((await Controller().SaveDesignerChanges("s1", new DesignerSaveRequest())).Result);

        Assert.Equal((StatusCodes.Status412PreconditionFailed, (object)"Saved by someone else."), (result.StatusCode, result.Value));
    }

    [Fact]
    public async Task SaveDesignerChanges_VersionRequired_AnswersPreconditionRequiredWithTheMessage()
    {
        _operations.Result = SiteBuilderOperationResult.VersionRequired("Send its ETag.");

        var result = Assert.IsType<ObjectResult>((await Controller().SaveDesignerChanges("s1", new DesignerSaveRequest())).Result);

        Assert.Equal((StatusCodes.Status428PreconditionRequired, (object)"Send its ETag."), (result.StatusCode, result.Value));
    }

    [Fact]
    public async Task SaveDesignerChanges_Request_IsHandedOverAsSentAndNeverAsADryRun()
    {
        _operations.Result = SiteBuilderOperationResult.Forbidden();
        var changes = new DesignerSaveRequest();
        var controller = Controller();
        controller.HttpContext.Request.Headers[ModuleConstants.Headers.SaveId] = "save-0001";
        controller.ModelState.AddModelError("value", string.Empty);
        controller.ModelState.AddModelError("catalogHash", "Bad hash.");

        await controller.SaveDesignerChanges("s1", changes, "abc12345");

        var request = Assert.IsType<DesignerSaveOperationRequest>(_operations.Request);
        Assert.Equal(("s1", "abc12345", "save-0001", false), (request.StoreId, request.CatalogHash, request.SaveId, request.DryRun));
        Assert.Same(changes, request.Changes);
        Assert.Equal(["The request could not be read.", "Bad hash."], request.BindingErrors);
        Assert.Equal("john", _operations.User.Identity!.Name);
    }

    [Fact]
    public async Task DiscardDesignerChanges_Discarded_AnswersTheResult()
    {
        var discarded = new ContentSaveResult { SaveId = "save-0001" };
        _operations.Result = SiteBuilderOperationResult.Success(discarded);
        var controller = Controller();
        controller.HttpContext.Request.Headers[ModuleConstants.Headers.SaveId] = "save-0001";

        var result = await controller.DiscardDesignerChanges("s1", new DesignerDiscardRequest());

        Assert.Same(discarded, Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("save-0001", Assert.IsType<DesignerDiscardOperationRequest>(_operations.Request).SaveId);
    }

    [Fact]
    public async Task DiscardDesignerChanges_Invalid_AnswersTheErrorAlone()
    {
        _operations.Result = SiteBuilderOperationResult.Invalid("Never published.");

        var result = await Controller().DiscardDesignerChanges("s1", new DesignerDiscardRequest());

        Assert.Equal("""{"error":"Never published."}""", PlatformJson.SerializeBody(Assert.IsType<BadRequestObjectResult>(result.Result)));
    }

    [Fact]
    public async Task GetDesignerThemeFile_Read_AnswersTheFile()
    {
        var file = new ThemeFileContent { Path = "default/sections/header-group.json", Content = "{}", ETag = "\"v\"" };
        _operations.Result = SiteBuilderOperationResult.Success(file);

        var result = await Controller().GetDesignerThemeFile("s1", file.Path);

        Assert.Same(file, Assert.IsType<OkObjectResult>(result.Result).Value);
        var request = Assert.IsType<DesignerThemeFileRequest>(_operations.Request);
        Assert.Equal(("s1", file.Path), (request.StoreId, request.Path));
    }

    [Fact]
    public async Task GetDesignerThemeFile_Forbidden_AnswersForbid()
    {
        _operations.Result = SiteBuilderOperationResult.Forbidden();

        Assert.IsType<ForbidResult>((await Controller().GetDesignerThemeFile("s1", "default/x.json")).Result);
    }

    private SiteBuilderController Controller()
    {
        var controller = new SiteBuilderController(
            storeService: null,
            pathResolver: null,
            blobContentStorageProviderFactory: null,
            publishingService: null,
            eventPublisher: null,
            gitContentOptions: null,
            gitHubConnectionService: null,
            gitContentPolicy: null,
            gitContentRepository: null,
            gitContentHistory: null,
            gitContentPublisher: null,
            gitLivePageService: null,
            settingsManager: null,
            authorizationService: null,
            distributedLock: null,
            requestValidator: null,
            contentHistory: null,
            themeDraftService: null,
            designerOperationService: _operations,
            contentPermissionService: null,
            filePageDraftService: null,
            themeStorageFactory: null,
            contentGuard: null);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "john")], "test")) },
        };

        return controller;
    }

    private sealed class StubDesignerOperations : IDesignerOperationService
    {
        public SiteBuilderOperationResult Result { get; set; }

        public SiteBuilderOperationRequest Request { get; private set; }

        public ClaimsPrincipal User { get; private set; }

        public Task<SiteBuilderOperationResult<ThemeFileContent>> ReadThemeFileAsync(DesignerThemeFileRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<ThemeFileContent>(request, user);

        public Task<SiteBuilderOperationResult<DesignerSaveResult>> SaveAsync(DesignerSaveOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<DesignerSaveResult>(request, user);

        public Task<SiteBuilderOperationResult<ContentSaveResult>> DiscardAsync(DesignerDiscardOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<ContentSaveResult>(request, user);

        public Task<SiteBuilderOperationResult> ValidateThemeFilesAsync(string storeId, IList<SiteBuilderContentFile> files, bool draft, string catalogHash,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);

        private Task<SiteBuilderOperationResult<T>> Answer<T>(SiteBuilderOperationRequest request, ClaimsPrincipal user)
        {
            Request = request;
            User = user;

            return Task.FromResult(Result as SiteBuilderOperationResult<T> ?? Result.As<T>());
        }
    }
}
