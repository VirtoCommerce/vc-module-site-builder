using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using VirtoCommerce.StoreModule.Core.Model;
using Xunit;
using Fakes = VirtoCommerce.SiteBuilder.Tests.PublishedRenameContentPreservationTests;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderQueryServicesTests
{
    private const string Schemas = "themes/s1/default/config/schemas";

    private readonly FakeBlobStorage _live = new();
    private readonly Fakes.FakeGroupedPageService _pages = new();
    private readonly FakeStoreService _stores = new(new Store { Id = "s1", SecureUrl = "https://shop.example/", DynamicProperties = [] },
        new Store { Id = "s2", DynamicProperties = [] });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User { get; } = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

    [Fact]
    public async Task ListTemplatesAsync_DefaultAndAlternateTemplates_TellsWhichArePublishedAndWhichHaveDrafts()
    {
        _live.Files["themes/s1/default/templates/product.json"] = "{}";
        _live.Files["themes/s1/default/templates/product.json-draft"] = "{}";
        _live.Files["themes/s1/default/templates/product.with-video.json-draft"] = "{}";
        _live.Files["themes/s1/default/templates/category.json"] = "{}";
        _live.Files["themes/s1/default/templates/notes.txt"] = "";
        _live.Files["themes/s1/default/templates/old/page.json"] = "{}";

        var result = await ContentQueries().ListTemplatesAsync(new SiteBuilderThemeQueryRequest { StoreId = "s1" }, User, Ct);

        Assert.Equal(
            """[{"key":"category","type":"category","isDefault":true,"published":true,"hasDraft":false},{"key":"product","type":"product","isDefault":true,"published":true,"hasDraft":true},{"key":"product.with-video","type":"product","isDefault":false,"published":false,"hasDraft":true}]""",
            PlatformJson.Serialize(result.Value));
    }

    [Fact]
    public async Task ListTemplatesAsync_UserOfAnotherStoreAskingForAnUnknownOne_IsForbiddenBeforeTheRequestIsChecked()
    {
        var request = new SiteBuilderThemeQueryRequest { StoreId = "unknown", BindingErrors = ["The value is not valid."] };

        var result = await ContentQueries(new FakeAuthorizationService("s2", Permissions.Read)).ListTemplatesAsync(request, User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task ListTemplatesAsync_ValueThatDidNotBind_IsInvalid()
    {
        var request = new SiteBuilderThemeQueryRequest { StoreId = "s1", BindingErrors = ["The value is not valid."] };

        var result = await ContentQueries().ListTemplatesAsync(request, User, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Invalid, "The value is not valid."), (result.Status, result.Error));
    }

    [Fact]
    public async Task GetSchemasCatalog_Schemas_AnswersTheCatalogWithoutInternalAndStaticEntries()
    {
        _live.Files[$"{Schemas}/sections/banner.json"] = """{ "name": "Banner", "description": "A banner. Use when: always.", "icon": "x" }""";
        _live.Files[$"{Schemas}/sections/page-settings.json"] = """{ "name": "Page settings", "static": true, "settings": [] }""";
        _live.Files[$"{Schemas}/sections/_internal.json"] = """{ "name": "Internal" }""";
        _live.Files[$"{Schemas}/templates/product.json"] = """{ "name": "Product", "previewUrl": "/p" }""";
        _live.Files[$"{Schemas}/shared/_sections.json"] = """{ "name": "Shared", "description": "Not listed." }""";

        var result = await Controller().GetSchemasCatalog("s1", null);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", content.ContentType);
        Assert.Equal(
            """{"sections":[{"key":"banner","name":"Banner","description":"A banner."}],"templates":[{"key":"product","name":"Product"}],"blocks":[],"objects":[],"shared":[{"key":"_sections","name":"Shared"}]}""",
            content.Content);
    }

    [Fact]
    public async Task GetSectionsSettings_Schemas_AnswersEachKindByKeyAsStored()
    {
        _live.Files[$"{Schemas}/sections/banner.json"] = """{ "name": "Banner" }""";
        _live.Files[$"{Schemas}/blocks/badge.json"] = """{"name":"Badge"}""";

        var result = await Controller().GetSectionsSettings("s1", null);

        Assert.Equal("""{ "sections": {"banner": { "name": "Banner" }}, "blocks": {"badge": {"name":"Badge"}}, "objects": {}, "shared": {} }""",
            Assert.IsType<ContentResult>(result).Content);
    }

    [Fact]
    public async Task GetTemplatesAndObjects_Schemas_AnswerThemAsTheEndpointsAlwaysHave()
    {
        _live.Files[$"{Schemas}/templates/product.json"] = """{ "name": "Product" }""";
        _live.Files[$"{Schemas}/objects/menu.json"] = """{ "name": "Menu" }""";

        var templates = await Controller().GetTemplates("s1", null);
        var objects = await Controller().GetObjects("s1", null);

        Assert.Equal(("""{"product": { "name": "Product" }}""", "application/json"), (Assert.IsType<ContentResult>(templates).Content, ((ContentResult)templates).ContentType));
        Assert.Equal("""{"menu": { "name": "Menu" }}""", Assert.IsType<OkObjectResult>(objects).Value);
    }

    [Theory]
    [InlineData("sections", "_internal")]
    [InlineData("sections", "missing")]
    public async Task GetSchemaByKey_InternalOrMissingSchema_IsNotFoundNamingIt(string kind, string key)
    {
        _live.Files[$"{Schemas}/sections/_internal.json"] = "{}";

        var result = await Controller().GetSchemaByKey("s1", null, kind, key);

        Assert.Equal($$"""{"kind":"{{kind}}","key":"{{key}}"}""", PlatformJson.SerializeBody(Assert.IsType<NotFoundObjectResult>(result)));
    }

    [Fact]
    public async Task GetSchemaAsync_SharedInternalSchema_IsServed()
    {
        _live.Files[$"{Schemas}/shared/_sections.json"] = """{ "settings": [] }""";

        var result = await ContentQueries().GetSchemaAsync(new SiteBuilderSchemaQueryRequest { StoreId = "s1", Kind = "shared", Key = "_sections" }, User, Ct);

        Assert.Equal("""{ "settings": [] }""", result.Value);
    }

    [Fact]
    public async Task GetSchemaByKey_Template_CarriesTheFieldsOfTheStaticSections()
    {
        _live.Files[$"{Schemas}/templates/product.json"] = """{ "name": "Product", "settings": [] }""";
        _live.Files[$"{Schemas}/sections/seo.json"] = """{ "static": "top", "settings": [ { "id": "title", "type": "string" } ] }""";

        var result = await Controller().GetSchemaByKey("s1", null, "templates", "product");

        Assert.Equal("""{"name":"Product","settings":[{"id":"title","type":"string"}]}""", Assert.IsType<ContentResult>(result).Content);
    }

    [Fact]
    public async Task GetSettings_NoBuilderSettings_AnswersAnEmptyObject()
    {
        var result = await Controller().GetSettings("s1", null);

        Assert.Equal(("{}", "application/json"), (Assert.IsType<ContentResult>(result).Content, ((ContentResult)result).ContentType));
    }

    [Fact]
    public async Task GetSettings_BuilderSettingsOfAStoreWithoutGit_AnswersTheFileAsStored()
    {
        _live.Files["themes/s1/default/config/builder_settings.json"] = """{ "a": 1 }""";

        var result = await Controller().GetSettings("s1", null);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/json", file.ContentType);
        Assert.Equal("""{ "a": 1 }""", await new StreamReader(file.FileStream).ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task GetSettings_BuilderSettingsOfAStoreOnGit_AnswersThemWithTheGitDescriptors()
    {
        _live.Files["themes/s1/default/config/builder_settings.json"] = """{ "a": 1, "publish": { "status": "old" } }""";

        var result = await Controller(git: true).GetSettings("s1", null);

        var settings = Newtonsoft.Json.Linq.JObject.Parse(Assert.IsType<ContentResult>(result).Content);
        Assert.Equal(1, (int)settings["a"]!);
        Assert.Equal("/api/site-builder/git/publish-status?storeId={{location.params.storeId}}&path={{path}}&type={{type}}", (string)settings["publish"]!["status"]);
    }

    [Fact]
    public async Task GetTemplate_FileBlobStorageDoesNotHold_IsNotFoundNamingWhereItLooked()
    {
        var result = await Controller().GetTemplate("s1", null, "about-us.page", "pages", draft: true);

        Assert.Equal("""{"basePath":"pages/s1","templatePath":"about-us.page"}""", PlatformJson.SerializeBody(Assert.IsType<NotFoundObjectResult>(result)));
    }

    [Fact]
    public async Task GetTemplate_DraftOfAFileInBlobStorage_AnswersItElseWhatIsLive()
    {
        _live.Files["pages/s1/about-us.page"] = """{ "live": true }""";

        var result = await Controller().GetTemplate("s1", null, "about-us.page", "pages", draft: true);

        Assert.Equal("""{ "live": true }""", await new StreamReader(Assert.IsType<FileStreamResult>(result).FileStream).ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task GetDesignerStorefront_StoreUserWorksIn_AnswersTheStoresUrl()
    {
        var result = await Controller().GetDesignerStorefront("s1");

        Assert.Equal("""{"url":"https://shop.example/","previewPath":"/designer-preview"}""", PlatformJson.SerializeBody(result));
    }

    [Fact]
    public async Task GitPreview_StoreWithoutGit_IsNotFound()
    {
        var result = await Controller().GitPreview("s1", "about-us.page", "pages");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetGroupedPagePreviewAsync_Page_AnswersTheStorefrontsPreviewOfIt()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "g1", StoreId = "s1", Pages = [] });

        var result = await Previews().GetGroupedPagePreviewAsync(new GroupedPageQueryRequest { GroupId = "g1" }, User, Ct);

        Assert.Equal("https://shop.example/designer-preview?pageId=g1", result.Value.Url);
    }

    [Fact]
    public async Task GetGroupedPagePreviewAsync_PageOfAnotherStoreOrNone_IsForbiddenOrNotFound()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "g1", StoreId = "s1", Pages = [] });
        var previews = Previews(new FakeAuthorizationService("s2", Permissions.Read));

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, (await previews.GetGroupedPagePreviewAsync(new GroupedPageQueryRequest { GroupId = "g1" }, User, Ct)).Status);
        Assert.Equal(SiteBuilderOperationStatus.NotFound, (await previews.GetGroupedPagePreviewAsync(new GroupedPageQueryRequest { GroupId = "g2" }, User, Ct)).Status);
    }

    [Fact]
    public async Task GetGroupedPagePreviewAsync_StoreWithoutAStorefrontUrl_IsInvalid()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "g1", StoreId = "s2", Pages = [] });

        var result = await Previews().GetGroupedPagePreviewAsync(new GroupedPageQueryRequest { GroupId = "g1" }, User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Contains("has no storefront url", result.Error);
    }

    [Theory]
    [InlineData("product", "https://shop.example/printers/epson")]
    [InlineData("product.with-video", "https://shop.example/printers/epson")]
    [InlineData("page", "https://shop.example/about")]
    public async Task GetTemplatePreviewAsync_TemplateOfAType_AnswersTheSamplePageItsSchemaNames(string key, string url)
    {
        _live.Files[$"{Schemas}/templates/product.json"] = """{ "previewUrl": "/printers/epson", "previewMessage": { "templateKey": "product" } }""";
        _live.Files[$"{Schemas}/templates/page.json"] = """{ "type": "pages", "previewUrl": "about" }""";

        var result = await Previews().GetTemplatePreviewAsync(new SiteBuilderTemplatePreviewRequest { StoreId = "s1", TemplateKey = key }, User, Ct);

        Assert.Equal(url, result.Value.Url);
    }

    [Theory]
    [InlineData("category", SiteBuilderOperationStatus.NotFound)]
    [InlineData("blog", SiteBuilderOperationStatus.Invalid)]
    public async Task GetTemplatePreviewAsync_TemplateWithoutASamplePageOrNotATemplate_IsRefused(string key, SiteBuilderOperationStatus status)
    {
        _live.Files[$"{Schemas}/templates/product.json"] = """{ "previewUrl": "/printers/epson" }""";

        var result = await Previews().GetTemplatePreviewAsync(new SiteBuilderTemplatePreviewRequest { StoreId = "s1", TemplateKey = key }, User, Ct);

        Assert.Equal(status, result.Status);
    }

    [Fact]
    public async Task GetTemplatePreviewAsync_UserOfAnotherStoreSendingABadKey_IsForbiddenFirst()
    {
        var request = new SiteBuilderTemplatePreviewRequest { StoreId = "s1", TemplateKey = "blog" };

        var result = await Previews(new FakeAuthorizationService("s2", Permissions.Read)).GetTemplatePreviewAsync(request, User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task GroupedPublishStatus_Page_WritesWhetherItIsPublishedAndHasChanges()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = "g1",
            StoreId = "s1",
            Pages = [new SiteBuilderPage { Id = "p1", StoreId = "s1", Status = ModuleConstants.PageStatuses.Published }, new SiteBuilderPage { Id = "p2", StoreId = "s1", Status = ModuleConstants.PageStatuses.Draft }],
        });

        var result = await PageController(new FakeAuthorizationService(true)).PublishStatus("g1");

        Assert.Equal("""{"published":true,"hasChanges":true}""", PlatformJson.SerializeBody(result));
    }

    [Fact]
    public async Task GroupedPublishStatus_PageOfAnotherStore_IsForbiddenWithAnEmptyBody()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "g1", StoreId = "s1", Pages = [] });

        var result = await PageController(new FakeAuthorizationService("s2", Permissions.Read)).PublishStatus("g1");

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal((StatusCodes.Status403Forbidden, "{}"), (forbidden.StatusCode, PlatformJson.Serialize(forbidden.Value)));
    }

    [Fact]
    public async Task GetFilePageStatusAsync_UserOfAnotherStoreAskingForAnUnknownOne_IsForbiddenBeforeTheRequestIsChecked()
    {
        var request = new SiteBuilderFilePageQueryRequest { StoreId = "unknown", Path = "/README.md" };

        var result = await PublishStatuses(new FakeAuthorizationService("s2", Permissions.Read)).GetFilePageStatusAsync(request, User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task GetFilePageStatusAsync_NoPath_TellsTheFlowOnly()
    {
        var result = await PublishStatuses().GetFilePageStatusAsync(new SiteBuilderFilePageQueryRequest { StoreId = "s1" }, User, Ct);

        Assert.Equal("""{"published":false,"hasChanges":false,"pending":false,"awaitingMerge":false,"flow":"blob","liveBehind":false}""", PlatformJson.Serialize(result.Value));
    }

    private SiteBuilderContentQueryService ContentQueries(FakeAuthorizationService authorization = null, bool git = false) =>
        new(authorization ?? new FakeAuthorizationService(true), RequestValidators.Content("s1", "s2"), _live, _live, _live, Themes(), new FakeGitContentPolicy(enabled: git),
            null, null, Options.Create(new GitContentOptions()));

    private SiteBuilderPreviewQueryService Previews(FakeAuthorizationService authorization = null) =>
        new(authorization ?? new FakeAuthorizationService(true), RequestValidators.Content("s1", "s2"), _stores, new FakeSettingsManager(), _pages,
            ContentQueries(authorization), new FakeGitContentPolicy(enabled: false), null, null, Options.Create(new GitContentOptions()));

    private SiteBuilderPublishStatusQueryService PublishStatuses(FakeAuthorizationService authorization = null) =>
        new(authorization ?? new FakeAuthorizationService(true), RequestValidators.Content("s1", "s2"), _pages, _live, new FakeGitContentPolicy(enabled: false),
            null, null, null, null, Options.Create(new GitContentOptions()));

    private ThemeStorageFactory Themes() => new(_stores, _live, _live, _live);

    private SiteBuilderController Controller(bool git = false)
    {
        var authorization = new FakeAuthorizationService(true);
        var controller = TestServices.CreateSiteBuilderController(
            storeService: _stores,
            pathResolver: _live,
            blobContentStorageProviderFactory: _live,
            publishingService: _live,
            eventPublisher: _live,
            gitContentOptions: Options.Create(new GitContentOptions()),
            gitHubConnectionService: null,
            gitContentPolicy: new FakeGitContentPolicy(enabled: git),
            gitContentRepository: null,
            gitContentHistory: null,
            gitContentPublisher: null,
            gitLivePageService: null,
            settingsManager: new FakeSettingsManager(),
            authorizationService: authorization,
            distributedLock: null,
            requestValidator: RequestValidators.Content("s1", "s2"),
            contentHistory: null,
            themeDraftService: null,
            designerOperationService: null,
            contentPermissionService: null,
            filePageDraftService: null,
            themeStorageFactory: Themes(),
            contentGuard: null,
            groupedPageService: _pages);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User } };

        return controller;
    }

    private SiteBuilderPageController PageController(FakeAuthorizationService authorization) =>
        new(_pages, null, authorization, null, null, Microsoft.Extensions.Logging.Abstractions.NullLogger<SiteBuilderPageController>.Instance, null,
            TestServices.PublishStatuses(authorization, _pages))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User } },
        };
}
