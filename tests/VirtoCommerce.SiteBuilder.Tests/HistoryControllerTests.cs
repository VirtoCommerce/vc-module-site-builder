using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using VirtoCommerce.Platform.Core.Common;
using Xunit;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;
using Fakes = VirtoCommerce.SiteBuilder.Tests.PublishedRenameContentPreservationTests;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests;

public class HistoryControllerTests
{
    private const string VersionId = "HA_v-Hs8f7RVPw9gHHqUAxmPDJ8";
    private const string Header = "default/sections/header-group.json";
    private const string Rules = "default/config/template_rules.json";
    private const string Template = "default/templates/product.json";
    private const string Page = """{"settings":{},"content":[]}""";
    private const string OldPage = """{"settings":{"name":"Old"},"content":[]}""";
    private const string SharedComponentPage = """{"settings":{},"content":[{"id":"p","type":"componentRef","componentRef":"c1"}]}""";

    private readonly FakeBlobStorage _live = new();
    private readonly Fakes.FakeGroupedPageService _pages = new();
    private readonly RecordingContentHistory _history = new();

    public HistoryControllerTests()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "g1", StoreId = "s1", Pages = [new SiteBuilderPage { Id = "p1", Status = Published }] });
        _pages.SeedContent("p1", Page);
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "other", StoreId = "s2", Pages = [] });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetStatus_Store_ReturnsItsStatus()
    {
        var result = await Controller().GetStatus("s1", Ct);

        Assert.Equal("""{"status":"enabled"}""", PlatformJson.SerializeBody(result.Result));
    }

    [Fact]
    public async Task GetStatus_StoreUserDoesNotWorkIn_ReturnsForbid()
    {
        var result = await Controller(new FakeAuthorizationService("s2", Permissions.Read)).GetStatus("s1", Ct);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetHistory_Items_ReturnsTheVersionPage()
    {
        var result = await Controller().GetHistory("s1", ["page:g1", $"theme:{Header}"], cancellationToken: Ct);

        Assert.Equal("""{"status":"enabled","versions":[]}""", PlatformJson.SerializeBody(result.Result));
    }

    [Fact]
    public async Task GetHistory_NoMoreVersions_LeavesTheCursorOutEvenWhereNullsAreWritten()
    {
        var result = await Controller().GetHistory("s1", ["page:g1"], cancellationToken: Ct);

        Assert.Equal("""{"status":"enabled","versions":[]}""", PlatformJson.SerializeBodyWithNulls(result.Result));
    }

    [Theory]
    [InlineData]
    [InlineData("page:")]
    [InlineData("pages:g1")]
    [InlineData("page:a/b")]
    [InlineData("theme:../x.json")]
    [InlineData("theme:product.json")]
    [InlineData("theme:default/snapshots/3/en-US.json")]
    public async Task GetHistory_MalformedItem_ReturnsBadRequest(params string[] items)
    {
        Assert.IsType<BadRequestObjectResult>((await Controller().GetHistory("s1", items, cancellationToken: Ct)).Result);
    }

    [Fact]
    public async Task GetHistory_TakeThatDidNotBind_ReturnsBadRequest()
    {
        // MVC passes the parameter's default on, so take=abc must not list a page of the default size.
        var controller = Controller();
        controller.ModelState.AddModelError("take", "The value 'abc' is not valid for take.");

        var result = await controller.GetHistory("s1", ["page:g1"], cancellationToken: Ct);

        Assert.Contains("abc", PlatformJson.ParseBody(Assert.IsType<BadRequestObjectResult>(result.Result)).Value<string>("error"));
        Assert.Empty(_history.Listed);
    }

    [Fact]
    public async Task GetHistory_StoreUserDoesNotWorkIn_ReturnsForbidBeforeBadRequest()
    {
        var controller = Controller(new FakeAuthorizationService("s2", Permissions.Read));

        Assert.IsType<ForbidResult>((await controller.GetHistory("s1", ["page:"], cancellationToken: Ct)).Result);
        Assert.IsType<ForbidResult>(await controller.GetVersion(VersionId, "s1", $"theme:{Header}", Ct));
        Assert.IsType<ForbidResult>((await controller.Restore(VersionId, "s1", [$"theme:{Header}"], Ct)).Result);
    }

    [Fact]
    public async Task GetHistory_PageOfAnotherStore_ReturnsForbid()
    {
        Assert.IsType<ForbidResult>((await Controller().GetHistory("s1", ["page:other"], cancellationToken: Ct)).Result);
    }

    [Fact]
    public async Task GetHistory_UnknownPage_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>((await Controller().GetHistory("s1", ["page:gone"], cancellationToken: Ct)).Result);
    }

    [Fact]
    public async Task GetHistory_GitHubFails_ThrowsGitContentException()
    {
        var github = new GitHubScript(("POST", "graphql", (_, _) => GitHubScript.Status(HttpStatusCode.BadGateway)));
        var history = SiteHistories.Create(github, new ThemeStorageFactory(_live.StoreService, _live, _live, _live), _pages);

        await Assert.ThrowsAsync<GitContentException>(() => Controller(history: history).GetHistory("s1", ["page:g1"], cancellationToken: Ct));
    }

    [Fact]
    public async Task GetVersion_Item_ReturnsItsContentAsOfTheVersion()
    {
        _history.Versions[(VersionId, $"theme:{Header}")] = "{}";

        var result = Assert.IsType<ContentResult>(await Controller().GetVersion(VersionId, "s1", $"theme:{Header}", Ct));

        Assert.Equal("{}", result.Content);
        Assert.Equal("application/json", result.ContentType);
    }

    [Fact]
    public async Task GetVersion_ItemMissingInTheVersion_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await Controller().GetVersion(VersionId, "s1", $"theme:{Header}", Ct));
    }

    [Fact]
    public async Task Restore_PageAndThemeFiles_WritesTheirDraftsAndRecordsOneVersion()
    {
        _live.Files[$"themes/s1/{Header}-draft"] = "current";
        _history.Versions[(VersionId, "page:g1")] = OldPage;
        _history.Versions[(VersionId, $"theme:{Header}")] = "{ \"content\": [] }";
        _history.Versions[(VersionId, $"theme:{Rules}")] = "{ \"version\": 2 }";
        var controller = Controller();
        controller.HttpContext.Request.Headers[ModuleConstants.Headers.SaveId] = "restore-0001";

        var result = await controller.Restore(VersionId, "s1", ["page:g1", $"theme:{Header}", $"theme:{Rules}"], Ct);

        Assert.Equal("""{"saveId":"restore-0001"}""", PlatformJson.SerializeBody(result.Result));
        Assert.Equal("{ \"content\": [] }", _live.Files[$"themes/s1/{Header}-draft"]);
        Assert.Equal("{ \"version\": 2 }", _live.Files[$"themes/s1/{Rules}-draft"]);
        Assert.Equal(OldPage, await DraftOfAsync("g1"));
        var (storeId, record) = Assert.Single(_history.Records);
        Assert.Equal("s1", storeId);
        Assert.Equal("Restored HA_v-Hs", record.Description);
        Assert.Equal("restore-0001", record.SaveId);
        Assert.Equal(["page:g1", $"theme:{Header}", $"theme:{Rules}"], record.Entries.Select(x => x.Item.ToString()));
    }

    [Theory]
    [InlineData("page:g1", Permissions.Update)]
    [InlineData("theme:" + Template, Permissions.Templates)]
    [InlineData("theme:" + Rules, Permissions.Templates)]
    [InlineData("theme:" + Header, Permissions.Theme)]
    public async Task Restore_Item_RequiresThePermissionThatEditsIt(string item, string permission)
    {
        _history.Versions[(VersionId, item)] = OldPage;
        var others = new[] { Permissions.Read, Permissions.Update, Permissions.Templates, Permissions.Theme }.Where(x => x != permission).ToArray();

        var refused = await Controller(new FakeAuthorizationService("s1", others)).Restore(VersionId, "s1", [item], Ct);

        Assert.IsType<ForbidResult>(refused.Result);
        Assert.Empty(_live.Files);
        Assert.Equal(Page, await DraftOfAsync("g1"));
        Assert.Empty(_history.Records);

        var restored = await Controller(new FakeAuthorizationService("s1", Permissions.Read, permission)).Restore(VersionId, "s1", [item], Ct);

        Assert.IsType<OkObjectResult>(restored.Result);
        Assert.Single(_history.Records);
    }

    [Fact]
    public async Task Restore_AnItemMissingInTheVersion_ReturnsNotFoundAndWritesNothing()
    {
        _history.Versions[(VersionId, $"theme:{Header}")] = "{}";

        var result = await Controller().Restore(VersionId, "s1", [$"theme:{Header}", $"theme:{Rules}"], Ct);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(_live.Files);
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task Restore_PageFailsAfterTheThemeFiles_PutsTheirDraftsBack()
    {
        _live.Files[$"themes/s1/{Header}-draft"] = "current";
        _history.Versions[(VersionId, "page:g1")] = OldPage;
        _history.Versions[(VersionId, $"theme:{Header}")] = "{}";
        _history.Versions[(VersionId, $"theme:{Rules}")] = "{}";
        _pages.SaveContentException = new KeyNotFoundException();

        var result = await Controller().Restore(VersionId, "s1", [$"theme:{Header}", $"theme:{Rules}", "page:g1"], Ct);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal("current", _live.Files[$"themes/s1/{Header}-draft"]);
        Assert.False(_live.Files.ContainsKey($"themes/s1/{Rules}-draft"));
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task Restore_NoEditPermission_ReturnsForbidBeforeNotFound()
    {
        var controller = Controller(new FakeAuthorizationService("s1", Permissions.Read));

        Assert.IsType<ForbidResult>((await controller.Restore(VersionId, "s1", ["page:gone"], Ct)).Result);
        Assert.IsType<ForbidResult>((await controller.Restore(VersionId, "s1", [$"theme:{Header}"], Ct)).Result);
    }

    [Fact]
    public async Task Restore_MalformedItemWithoutEditPermission_ReturnsBadRequest()
    {
        var result = await Controller(new FakeAuthorizationService("s1", Permissions.Read)).Restore(VersionId, "s1", ["page:a/b"], Ct);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("theme:default/catalogs/abc12345.json")]
    [InlineData("theme:default/catalogs/current.json")]
    [InlineData("theme:default/config/settings_schema.json")]
    [InlineData("theme:default/layout/theme.liquid")]
    public async Task Restore_ThemeFileTheBuilderDoesNotSave_ReturnsBadRequestAndWritesNothing(string item)
    {
        _history.Versions[(VersionId, item)] = "{}";

        var result = await Controller().Restore(VersionId, "s1", [item], Ct);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(_live.Files);
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task Restore_PagePlacingASharedComponentWithoutSharedComponentsRead_ReturnsForbidAndWritesNothing()
    {
        _history.Versions[(VersionId, "page:g1")] = SharedComponentPage;
        _history.Versions[(VersionId, $"theme:{Header}")] = "{}";

        var result = await Controller(new FakeAuthorizationService("s1", Permissions.Read, Permissions.Update, Permissions.Theme))
            .Restore(VersionId, "s1", ["page:g1", $"theme:{Header}"], Ct);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Equal(Page, await DraftOfAsync("g1"));
        Assert.Empty(_live.Files);
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task Restore_PagePlacingASharedComponentWithSharedComponentsRead_RestoresIt()
    {
        _history.Versions[(VersionId, "page:g1")] = SharedComponentPage;

        var result = await Controller(new FakeAuthorizationService("s1", Permissions.Read, Permissions.Update, Permissions.SharedComponentsRead))
            .Restore(VersionId, "s1", ["page:g1"], Ct);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(SharedComponentPage, await DraftOfAsync("g1"));
    }

    [Fact]
    public async Task Restore_TwoPages_ReturnsBadRequest()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "g2", StoreId = "s1", Pages = [] });

        Assert.IsType<BadRequestObjectResult>((await Controller().Restore(VersionId, "s1", ["page:g1", "page:g2"], Ct)).Result);
    }

    [Fact]
    public async Task RestoreOperation_NoEditPermission_IsForbiddenBeforeTheUnknownVersionIsLookedUp()
    {
        var request = RestoreRequest("HA_v-Hs8f7RVPw9gHHqUAxmPDJ9", $"theme:{Header}");

        var refused = await Operations(new FakeAuthorizationService("s1", Permissions.Read)).RestoreAsync(request, User, Ct);
        var notFound = await Operations().RestoreAsync(request, User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, refused.Status);
        Assert.Equal(SiteBuilderOperationStatus.NotFound, notFound.Status);
    }

    [Fact]
    public async Task RestoreOperation_DryRun_AnswersWhatItWouldOverwrite_AndWritesNothing()
    {
        _live.Files[$"themes/s1/{Header}-draft"] = "current";
        _history.Versions[(VersionId, "page:g1")] = OldPage;
        _history.Versions[(VersionId, $"theme:{Header}")] = "{ \"content\": [] }";
        var request = RestoreRequest(VersionId, "page:g1", $"theme:{Header}");
        request.DryRun = true;

        var result = await Operations().RestoreAsync(request, User, Ct);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value);
        Assert.False(result.AffectsPublished);
        var page = Assert.Single(result.Preview.Objects, x => x.ObjectType == ModuleConstants.ChangePreview.Page);
        Assert.Equal("g1", page.ObjectId);
        Assert.NotNull(page.ETag);
        Assert.Equal((Page, OldPage), (Assert.Single(page.Fields).From, page.Fields[0].To));
        var theme = Assert.Single(result.Preview.Objects, x => x.ObjectType == ModuleConstants.ChangePreview.ThemeFile);
        Assert.Equal(Header, theme.ObjectId);
        Assert.Equal(("current", "{ \"content\": [] }"), (Assert.Single(theme.Fields).From, theme.Fields[0].To));
        Assert.Equal("current", _live.Files[$"themes/s1/{Header}-draft"]);
        Assert.Equal(Page, await DraftOfAsync("g1"));
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task RestoreOperation_DraftChangedSinceTheExpectedVersion_IsAConflict_AndWritesNothing()
    {
        _live.Files[$"themes/s1/{Header}-draft"] = "current";
        _history.Versions[(VersionId, "page:g1")] = OldPage;
        _history.Versions[(VersionId, $"theme:{Header}")] = "{ \"content\": [] }";
        var dryRun = RestoreRequest(VersionId, "page:g1", $"theme:{Header}");
        dryRun.DryRun = true;
        var versions = (await Operations().RestoreAsync(dryRun, User, Ct)).Preview.Objects
            .ToDictionary(x => x.ObjectType == ModuleConstants.ChangePreview.Page ? $"page:{x.ObjectId}" : $"theme:{x.ObjectId}", x => x.ETag);
        _live.Files[$"themes/s1/{Header}-draft"] = "changed meanwhile";

        var request = RestoreRequest(VersionId, "page:g1", $"theme:{Header}");
        request.ExpectedVersions = versions;
        var result = await Operations().RestoreAsync(request, User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.Equal("changed meanwhile", _live.Files[$"themes/s1/{Header}-draft"]);
        Assert.Equal(Page, await DraftOfAsync("g1"));
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task RestoreOperation_PageChangedSinceTheExpectedVersion_IsAConflict_AndPutsTheThemeDraftsBack()
    {
        _live.Files[$"themes/s1/{Header}-draft"] = "current";
        _history.Versions[(VersionId, "page:g1")] = OldPage;
        _history.Versions[(VersionId, $"theme:{Header}")] = "{ \"content\": [] }";
        var request = RestoreRequest(VersionId, $"theme:{Header}", "page:g1");
        request.ExpectedVersions = new Dictionary<string, string> { ["page:g1"] = "a version the page is not at" };

        var result = await Operations().RestoreAsync(request, User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.Equal("current", _live.Files[$"themes/s1/{Header}-draft"]);
        Assert.Equal(Page, await DraftOfAsync("g1"));
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task RestoreOperation_DraftsAtTheExpectedVersions_RestoresThem()
    {
        _live.Files[$"themes/s1/{Header}-draft"] = "current";
        _history.Versions[(VersionId, "page:g1")] = OldPage;
        _history.Versions[(VersionId, $"theme:{Header}")] = "{ \"content\": [] }";
        var dryRun = RestoreRequest(VersionId, "page:g1", $"theme:{Header}");
        dryRun.DryRun = true;
        var versions = (await Operations().RestoreAsync(dryRun, User, Ct)).Preview.Objects
            .ToDictionary(x => x.ObjectType == ModuleConstants.ChangePreview.Page ? $"page:{x.ObjectId}" : $"theme:{x.ObjectId}", x => x.ETag);

        var request = RestoreRequest(VersionId, "page:g1", $"theme:{Header}");
        request.ExpectedVersions = versions;
        var result = await Operations().RestoreAsync(request, User, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal("{ \"content\": [] }", _live.Files[$"themes/s1/{Header}-draft"]);
        Assert.Equal(OldPage, await DraftOfAsync("g1"));
        Assert.Single(_history.Records);
    }

    [Fact]
    public async Task Restore_DraftConflict_Is412WithTheMessage()
    {
        _history.Versions[(VersionId, "page:g1")] = OldPage;
        _pages.SaveContentException = new SiteBuilderContentConflictException();

        var result = await Controller().Restore(VersionId, "s1", ["page:g1"], Ct);

        var conflict = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status412PreconditionFailed, conflict.StatusCode);
        Assert.Equal(new SiteBuilderContentConflictException().Message, conflict.Value);
    }

    private static ClaimsPrincipal User => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

    private static ContentHistoryRestoreOperationRequest RestoreRequest(string versionId, params string[] items) =>
        new() { StoreId = "s1", VersionId = versionId, Items = items, SaveId = "restore-0001" };

    private async Task<string> DraftOfAsync(string groupId) =>
        (await _pages.LoadGroupContentAsync(await _pages.GetByIdAsync(groupId), cancellationToken: Ct)).Content;

    private HistoryController Controller(IAuthorizationService authorization = null, IContentHistory history = null)
    {
        var controller = new HistoryController(Operations(authorization, history));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = User },
        };
        return controller;
    }

    private ContentHistoryOperationService Operations(IAuthorizationService authorization = null, IContentHistory history = null)
    {
        authorization ??= new FakeAuthorizationService(true);
        history ??= _history;
        var drafts = new ThemeDraftService(new ThemeStorageFactory(_live.StoreService, _live, _live, _live), new InProcessDistributedLock(), _live,
            NullLogger<ThemeDraftService>.Instance);
        return TestServices.ContentHistoryOperations(authorization, history, _pages, TestServices.Designer(history, themeDrafts: drafts, pages: _pages, events: _live));
    }
}
