using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

/// <summary>The page and multilanguage page endpoints answer each outcome of an operation with the response they always gave it.</summary>
public class PageControllerResponseTests
{
    private readonly StubOperations _operations = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpdateGroup_Saved_AnswersThePage()
    {
        var page = new GroupedSiteBuilderPage { Id = "g1" };
        _operations.Result = SiteBuilderOperationResult.Success(page);

        var result = await PageController().UpdateGroup(page, Ct);

        Assert.Same(page, Assert.IsType<OkObjectResult>(result.Result).Value);
        var request = Assert.IsType<GroupedPageSaveOperationRequest>(_operations.Request);
        Assert.Equal((page, false), (request.Page, request.DryRun));
        Assert.Equal("john", _operations.User.Identity!.Name);
    }

    [Fact]
    public async Task UpdateGroup_Forbidden_AnswersAnEmptyObject()
    {
        _operations.Result = SiteBuilderOperationResult.Forbidden();

        var result = Assert.IsType<ObjectResult>((await PageController().UpdateGroup(new GroupedSiteBuilderPage(), Ct)).Result);

        Assert.Equal((StatusCodes.Status403Forbidden, "{}"), (result.StatusCode, PlatformJson.SerializeBody(result)));
    }

    [Fact]
    public async Task UpdateGroup_Invalid_AnswersTheErrorAsText()
    {
        _operations.Result = SiteBuilderOperationResult.Invalid("Archived page cannot be updated.");

        var result = await PageController().UpdateGroup(new GroupedSiteBuilderPage(), Ct);

        Assert.Equal("Archived page cannot be updated.", Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task CreateGroupPage_Created_AnswersThePage()
    {
        var page = new GroupedSiteBuilderPage { Id = "g1" };
        _operations.Result = SiteBuilderOperationResult.Success(page);
        var body = new CreateGroupedPageRequest();

        var result = await PageController().CreateGroupPage(body, Ct);

        Assert.Same(page, Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Same(body, Assert.IsType<GroupedPageCreateOperationRequest>(_operations.Request).Page);
    }

    [Fact]
    public async Task CreateGroup_Invalid_AnswersTheErrorAsText()
    {
        _operations.Result = SiteBuilderOperationResult.Invalid("The page is missing or invalid.");

        var result = await PageController().CreateGroup(null, Ct);

        Assert.Equal("The page is missing or invalid.", Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task PublishGroup_Published_AnswersOkWithoutBody()
    {
        _operations.Result = SiteBuilderOperationResult.Success(affectsPublished: true);
        var controller = PageController();
        controller.HttpContext.Request.Headers[ModuleConstants.Headers.SaveId] = "save-0001";

        Assert.IsType<OkResult>(await controller.PublishGroup("g1", publish: true, Ct));

        var request = Assert.IsType<GroupedPagePublishOperationRequest>(_operations.Request);
        Assert.Equal(("g1", true, "save-0001", false), (request.GroupId, request.Publish, request.SaveId, request.DryRun));
    }

    [Fact]
    public async Task PublishGroup_NotFound_AnswersNotFoundWithoutBody()
    {
        _operations.Result = SiteBuilderOperationResult.NotFound();

        Assert.IsType<NotFoundResult>(await PageController().PublishGroup("g1", publish: false, Ct));
    }

    [Fact]
    public async Task DiscardGroupDraft_Invalid_AnswersTheErrorInAnObject()
    {
        _operations.Result = SiteBuilderOperationResult.Invalid("Never published.");

        var result = await PageController().DiscardGroupDraft("g1", Ct);

        Assert.Equal("""{"error":"Never published."}""", PlatformJson.SerializeBody(Assert.IsType<BadRequestObjectResult>(result)));
    }

    [Fact]
    public async Task DiscardGroupDraft_Forbidden_AnswersAnEmptyObject()
    {
        _operations.Result = SiteBuilderOperationResult.Forbidden();

        var result = Assert.IsType<ObjectResult>(await PageController().DiscardGroupDraft("g1", Ct));

        Assert.Equal((StatusCodes.Status403Forbidden, "{}"), (result.StatusCode, PlatformJson.SerializeBody(result)));
    }

    [Fact]
    public async Task DiscardGroupDraft_Discarded_AnswersOkWithoutBody()
    {
        _operations.Result = SiteBuilderOperationResult.Success();

        Assert.IsType<OkResult>(await PageController().DiscardGroupDraft("g1", Ct));
    }

    [Fact]
    public async Task SavePageContent_Saved_AnswersNoContentWithTheVersionInTheHeader()
    {
        _operations.Result = SiteBuilderOperationResult.Success(new UpdatePageContentResult { ETag = "\"v2\"" });
        var controller = PageController("""{"settings":{},"content":[]}""");

        var result = await controller.SavePageContent("g1", "\"v1\"", includeVersion: false, Ct);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("\"v2\"", controller.Response.Headers.ETag.ToString());
        var request = Assert.IsType<GroupedPageContentSaveOperationRequest>(_operations.Request);
        Assert.Equal(("g1", "\"v1\"", null), (request.GroupId, request.IfMatch, request.ETag));
        Assert.Equal("""{"settings":{},"content":[]}""", await request.ContentReader(Ct));
    }

    [Fact]
    public async Task SavePageContentJson_SavedWithVersion_AnswersTheVersion()
    {
        var version = new UpdatePageContentResult { ETag = "\"v2\"" };
        _operations.Result = SiteBuilderOperationResult.Success(version);
        var controller = PageController();

        var result = await controller.SavePageContentJson("g1", new UpdatePageContentRequest { Content = "{}", ETag = "\"v1\"" }, null, includeVersion: true, Ct);

        Assert.Same(version, Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("\"v2\"", controller.Response.Headers.ETag.ToString());
        Assert.Equal(("{}", "\"v1\""), (((GroupedPageContentSaveOperationRequest)_operations.Request).Content, ((GroupedPageContentSaveOperationRequest)_operations.Request).ETag));
    }

    [Fact]
    public async Task SavePageContentJson_Conflict_AnswersPreconditionFailedWithTheMessageAndNoVersion()
    {
        _operations.Result = SiteBuilderOperationResult.Conflict("Saved by someone else.");
        var controller = PageController();

        var result = Assert.IsType<ObjectResult>(await controller.SavePageContentJson("g1", new UpdatePageContentRequest(), "\"v1\"", false, Ct));

        Assert.Equal((StatusCodes.Status412PreconditionFailed, (object)"Saved by someone else."), (result.StatusCode, result.Value));
        Assert.Empty(controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task SavePageContentJson_VersionRequired_AnswersPreconditionRequiredWithTheMessage()
    {
        _operations.Result = SiteBuilderOperationResult.VersionRequired("Send its ETag.");

        var result = Assert.IsType<ObjectResult>(await PageController().SavePageContentJson("g1", null, null, false, Ct));

        Assert.Equal((StatusCodes.Status428PreconditionRequired, (object)"Send its ETag."), (result.StatusCode, result.Value));
    }

    [Fact]
    public async Task CopyPageContent_Copied_AnswersNoContent()
    {
        _operations.Result = SiteBuilderOperationResult.Success();

        Assert.IsType<NoContentResult>(await PageController().CopyPageContent("target", "source", Ct));

        var request = Assert.IsType<GroupedPageContentCopyOperationRequest>(_operations.Request);
        Assert.Equal(("target", "source"), (request.GroupId, request.SourceGroupId));
    }

    [Fact]
    public async Task MultilanguageCreate_Invalid_AnswersTheErrorAsText()
    {
        _operations.Result = SiteBuilderOperationResult.Invalid("Store 'x' does not exist.");

        var result = await MultilanguageController().Create(new MultilanguagePage());

        Assert.Equal("Store 'x' does not exist.", Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task MultilanguageConvert_ConcurrentChange_AnswersConflictWithTheMessage()
    {
        _operations.Result = SiteBuilderOperationResult.ConcurrentChange("Changed by another request.");

        var result = await MultilanguageController().Convert("g1", Ct);

        Assert.Equal("Changed by another request.", Assert.IsType<ConflictObjectResult>(result.Result).Value);
        Assert.Equal("g1", Assert.IsType<GroupedPageOperationRequest>(_operations.Request).GroupId);
    }

    [Fact]
    public async Task MultilanguageLink_Forbidden_AnswersAnEmptyObject()
    {
        _operations.Result = SiteBuilderOperationResult.Forbidden();

        var result = Assert.IsType<ObjectResult>((await MultilanguageController().Link("root", "g1", Ct)).Result);

        Assert.Equal((StatusCodes.Status403Forbidden, "{}"), (result.StatusCode, PlatformJson.SerializeBody(result)));
        var request = Assert.IsType<MultilanguagePageLinkOperationRequest>(_operations.Request);
        Assert.Equal(("root", "g1", false), (request.MultilanguagePageId, request.GroupId, request.DryRun));
    }

    [Fact]
    public async Task MultilanguagePublishAll_Published_AnswersTheSummary()
    {
        var summary = new MultilanguagePageBatchResult();
        _operations.Result = SiteBuilderOperationResult.Success(summary, affectsPublished: true);

        var result = await MultilanguageController().PublishAll("root", Ct);

        Assert.Same(summary, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task MultilanguageCreateLanguage_NotFound_AnswersNotFoundWithoutBody()
    {
        _operations.Result = SiteBuilderOperationResult.NotFound();

        Assert.IsType<NotFoundResult>((await MultilanguageController().CreateLanguage("root", "de-DE", copyContent: false, Ct)).Result);

        var request = Assert.IsType<MultilanguagePageLanguageOperationRequest>(_operations.Request);
        Assert.Equal(("root", "de-DE", false), (request.MultilanguagePageId, request.CultureName, request.CopyContent));
    }

    private SiteBuilderPageController PageController(string body = "")
    {
        var controller = new SiteBuilderPageController(null, null, null, null, null, NullLogger<SiteBuilderPageController>.Instance, _operations, TestServices.PublishStatuses(null, null));
        var httpContext = new DefaultHttpContext { User = User() };
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private MultilanguagePageController MultilanguageController() =>
        new(null, null, null, _operations) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User() } } };

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "john")], "test"));

    private sealed class StubOperations : IGroupedPageOperationService, IMultilanguagePageOperationService
    {
        public SiteBuilderOperationResult Result { get; set; }

        public object Request { get; private set; }

        public ClaimsPrincipal User { get; private set; }

        public Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateAsync(GroupedPageSaveOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<GroupedSiteBuilderPage>(request, user);

        public Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateWithContentAsync(GroupedPageCreateOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<GroupedSiteBuilderPage>(request, user);

        public Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> UpdateAsync(GroupedPageSaveOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<GroupedSiteBuilderPage>(request, user);

        public Task<SiteBuilderOperationResult> PublishAsync(GroupedPagePublishOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer(request, user);

        public Task<SiteBuilderOperationResult> DiscardDraftAsync(GroupedPageDiscardOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer(request, user);

        public Task<SiteBuilderOperationResult<UpdatePageContentResult>> SaveContentAsync(GroupedPageContentSaveOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<UpdatePageContentResult>(request, user);

        public Task<SiteBuilderOperationResult<UpdatePageContentResult>> SaveRawContentAsync(GroupedPageContentSaveOperationRequest request,
            ClaimsPrincipal user, CancellationToken cancellationToken = default) => Answer<UpdatePageContentResult>(request, user);

        public Task<SiteBuilderOperationResult> CopyContentAsync(GroupedPageContentCopyOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer(request, user);

        public Task<SiteBuilderOperationResult<MultilanguagePage>> CreateAsync(MultilanguagePageSaveOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<MultilanguagePage>(request, user);

        public Task<SiteBuilderOperationResult<MultilanguagePage>> UpdateAsync(MultilanguagePageSaveOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<MultilanguagePage>(request, user);

        public Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateLanguageAsync(MultilanguagePageLanguageOperationRequest request,
            ClaimsPrincipal user, CancellationToken cancellationToken = default) => Answer<GroupedSiteBuilderPage>(request, user);

        public Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> LinkAsync(MultilanguagePageLinkOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<GroupedSiteBuilderPage>(request, user);

        public Task<SiteBuilderOperationResult<MultilanguagePage>> ConvertAsync(GroupedPageOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<MultilanguagePage>(request, user);

        public Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> MarkUpToDateAsync(GroupedPageOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<GroupedSiteBuilderPage>(request, user);

        public Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> UnlinkAsync(GroupedPageOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<GroupedSiteBuilderPage>(request, user);

        public Task<SiteBuilderOperationResult<IList<string>>> ResetOverridesAsync(MultilanguagePageResetOverridesOperationRequest request,
            ClaimsPrincipal user, CancellationToken cancellationToken = default) => Answer<IList<string>>(request, user);

        public Task<SiteBuilderOperationResult<MultilanguagePage>> CloneAsync(MultilanguagePageOperationRequest request, ClaimsPrincipal user,
            CancellationToken cancellationToken = default) => Answer<MultilanguagePage>(request, user);

        public Task<SiteBuilderOperationResult<MultilanguagePageBatchResult>> PublishAllAsync(MultilanguagePageOperationRequest request,
            ClaimsPrincipal user, CancellationToken cancellationToken = default) => Answer<MultilanguagePageBatchResult>(request, user);

        private Task<SiteBuilderOperationResult> Answer(object request, ClaimsPrincipal user)
        {
            Request = request;
            User = user;

            return Task.FromResult(Result);
        }

        private Task<SiteBuilderOperationResult<T>> Answer<T>(object request, ClaimsPrincipal user)
        {
            Request = request;
            User = user;

            return Task.FromResult(Result as SiteBuilderOperationResult<T> ?? Result.As<T>());
        }
    }
}
