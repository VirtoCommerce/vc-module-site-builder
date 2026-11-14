using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;
using Xunit;
using Fakes = VirtoCommerce.SiteBuilder.Tests.PublishedRenameContentPreservationTests;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests;

public class DesignerOperationServiceTests
{
    private const string Catalog = "abc12345";
    private const string Header = "default/sections/header-group.json";
    private const string Published = """{"settings":{},"content":[]}""";
    private const string Edited = """{"settings":{"name":"Home"},"content":[{"id":"a","type":"text"}]}""";
    private const string HeaderFile = """{ "content": [ { "id": "h", "type": "site-header" } ] }""";
    private const string Rules = "default/config/template_rules.json";
    private const string RulesFile = """{ "version": 2, "conditions": [] }""";
    private const string NotRead = "The request could not be read.";

    private readonly FakeBlobStorage _live = new();
    private readonly Fakes.FakeGroupedPageService _pages = new();
    private readonly RecordingContentHistory _history = new();
    private readonly InProcessDistributedLock _lock = new();
    private readonly ThemeStorageFactory _themes;
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

    public DesignerOperationServiceTests()
    {
        _themes = new ThemeStorageFactory(_live.StoreService, _live, _live, _live);
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "g1", StoreId = "s1", Pages = [new SiteBuilderPage { Id = "p1", StoreId = "s1", Status = ModuleConstants.PageStatuses.Published }] });
        _pages.SeedContent("p1", Published);
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "other", StoreId = "s2", Pages = [] });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SaveAsync_StoreUserDoesNotWorkIn_IsForbiddenBeforeTheRequestIsRead()
    {
        var request = SaveRequest(new DesignerSaveRequest());
        request.BindingErrors = [NotRead];

        var result = await Operations(new FakeAuthorizationService("s2", Permissions.Read, Permissions.Update)).SaveAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task SaveAsync_ThemeFileWithoutThePermissionThatEditsIt_IsForbiddenBeforeAnInvalidBody()
    {
        // a page named both ways is refused by the request validator
        var changes = Save(new DesignerSavePage { GroupId = "g1", Path = "about.page", Type = "pages", Content = Edited }, (Header, HeaderFile));

        var refused = await Operations(new FakeAuthorizationService("s1", Permissions.Read, Permissions.Update)).SaveAsync(SaveRequest(changes), _user, Ct);
        var invalid = await Operations(new FakeAuthorizationService("s1", Permissions.Read, Permissions.Update, Permissions.Theme)).SaveAsync(SaveRequest(changes), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, refused.Status);
        Assert.Equal(SiteBuilderOperationStatus.Invalid, invalid.Status);
    }

    [Fact]
    public async Task SaveAsync_InvalidBody_IsRefusedBeforeThePageIsLookedUp()
    {
        var page = new DesignerSavePage { GroupId = "gone", ETag = DatabasePage().ETag, Content = """{"content":[]}""" };

        var result = await Operations().SaveAsync(SaveRequest(Save(page)), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task SaveAsync_UnknownPage_IsNotFoundBeforeWhatCouldNotBeRead()
    {
        var request = SaveRequest(Save(new DesignerSavePage { GroupId = "gone", ETag = DatabasePage().ETag, Content = Edited }));
        request.BindingErrors = [NotRead];

        var result = await Operations().SaveAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task SaveAsync_PageOfAnotherStore_IsForbiddenOnceItIsLookedUp()
    {
        var request = SaveRequest(Save(new DesignerSavePage { GroupId = "other", ETag = DatabasePage().ETag, Content = Edited }));
        request.BindingErrors = [NotRead];

        var result = await Operations().SaveAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
        AssertNothingWritten();
    }

    [Fact]
    public async Task SaveAsync_WhatCouldNotBeRead_IsRefusedAfterThePageAccess()
    {
        var request = SaveRequest(Save(DatabasePage()));
        request.BindingErrors = [NotRead, "Bad date."];

        var result = await Operations().SaveAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Equal($"{NotRead} Bad date.", result.Error);
        Assert.Null(result.Errors);
        AssertNothingWritten();
    }

    [Fact]
    public async Task SaveAsync_PageWithoutVersion_RequiresItBeforeTheThemeFilesAreChecked()
    {
        var page = DatabasePage();
        page.ETag = null;

        var result = await Operations().SaveAsync(SaveRequest(Save(page, (Header, HeaderFile)), catalogHash: null), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.VersionRequired, result.Status);
        Assert.Equal("Read the page content and send its ETag before saving.", result.Error);
    }

    [Fact]
    public async Task SaveAsync_ThemeFileThatDoesNotFitTheCatalog_IsInvalidWithEachError()
    {
        await SaveCatalogAsync();

        var result = await Operations().SaveAsync(SaveRequest(Save(null, (Header, """{ "content": [ { "id": "h", "type": "unknown" } ] }"""))), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.NotEmpty(result.Errors);
        AssertNothingWritten();
    }

    [Fact]
    public async Task SaveAsync_DryRun_AnswersTheSanitizedContentOverTheDraftsAndWritesNothing()
    {
        await SaveContentCatalogAsync();
        const string liveHeader = """{ "content": [] }""";
        _live.Files[Theme(Header)] = liveHeader;
        var page = DatabasePage();
        page.Content = """{"settings":{},"content":[{"id":"a","type":"notice","body":"<p>Hi</p><script>alert(1)</script>"}]}""";
        const string header = """{ "content": [ { "id": "h", "type": "notice", "note": { "en-US": "<a href=\"javascript:alert(1)\">x</a>" } } ] }""";
        var request = SaveRequest(Save(page, (Header, header)));
        request.DryRun = true;

        var result = await Operations().SaveAsync(request, _user, Ct);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value);
        var (pageChange, headerChange) = (result.Preview.Objects[0], result.Preview.Objects[1]);
        Assert.Equal((ModuleConstants.ChangePreview.Page, "g1", page.ETag), (pageChange.ObjectType, pageChange.ObjectId, pageChange.ETag));
        var pageContent = Assert.Single(pageChange.Fields);
        Assert.Equal((ModuleConstants.ChangePreview.Content, Published), (pageContent.Name, pageContent.From));
        Assert.Equal("""{"settings":{},"content":[{"id":"a","type":"notice","body":"<p>Hi</p>"}]}""", pageContent.To);
        Assert.Equal((ModuleConstants.ChangePreview.ThemeFile, Header, ETagOf(Header)), (headerChange.ObjectType, headerChange.ObjectId, headerChange.ETag));
        var headerContent = Assert.Single(headerChange.Fields);
        Assert.Equal(liveHeader, headerContent.From);
        Assert.Equal("<a>x</a>", JObject.Parse(headerContent.To)["content"]![0]!["note"]!.Value<string>("en-US"));
        AssertNothingWritten();
        Assert.Empty(_live.Events.OfType<SiteBuilderContentChangedEvent>());
        Assert.Empty(_live.Events.OfType<SiteBuilderPageChangedEvent>());
    }

    [Fact]
    public async Task SaveAsync_DryRunOfAFilePage_AnswersWhatIsPublishedAndWritesNoDraft()
    {
        await SaveContentCatalogAsync();
        const string published = """{"settings":{"type":"settings"},"content":[]}""";
        _live.Files[Page("about.page")] = published;
        const string about = """{"settings":{"type":"settings"},"content":[{"id":"a","type":"notice","body":"<p onclick=\"alert(1)\">About</p>"}]}""";
        var request = SaveRequest(Save(new DesignerSavePage { Path = "about.page", Type = "pages", Content = about }));
        request.DryRun = true;

        var result = await Operations().SaveAsync(request, _user, Ct);

        var change = Assert.Single(result.Preview.Objects);
        Assert.Equal((ModuleConstants.ChangePreview.FilePage, "about.page"), (change.ObjectType, change.ObjectId));
        var content = Assert.Single(change.Fields);
        Assert.Equal(published, content.From);
        Assert.Equal(PageJson.Serialize(JToken.Parse(about.Replace(" onclick=\\\"alert(1)\\\"", ""))), content.To);
        Assert.False(_live.Files.ContainsKey(Page("about.page-draft")));
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task SaveAsync_DryRunOfContentThatCanRunScript_IsInvalidAsTheSaveIs()
    {
        await SaveCatalogAsync();
        var page = DatabasePage();
        page.Content = """{"settings":{},"content":[{"id":"a","type":"text","buttons":[{"url":"javascript:alert(1)"}]}]}""";
        var request = SaveRequest(Save(page));
        request.DryRun = true;

        var result = await Operations().SaveAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Contains("javascript: URL", result.Error);
        AssertNothingWritten();
    }

    [Fact]
    public async Task DiscardAsync_StoreUserDoesNotWorkIn_IsForbiddenBeforeTheRequestIsRead()
    {
        var request = new DesignerDiscardOperationRequest { StoreId = "s1", Changes = new DesignerDiscardRequest(), BindingErrors = [NotRead] };

        var result = await Operations(new FakeAuthorizationService("s2", Permissions.Read, Permissions.Update)).DiscardAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task DiscardAsync_UnknownPage_IsNotFoundBeforeWhatCouldNotBeRead()
    {
        var request = new DesignerDiscardOperationRequest
        {
            StoreId = "s1",
            Changes = new DesignerDiscardRequest { Page = new DesignerSavePage { GroupId = "gone" } },
            BindingErrors = [NotRead],
        };

        var result = await Operations().DiscardAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task DiscardAsync_PageNeverPublished_IsInvalid()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "new", StoreId = "s1", Pages = [new SiteBuilderPage { Id = "d1", StoreId = "s1", Status = ModuleConstants.PageStatuses.Draft }] });
        var request = new DesignerDiscardOperationRequest { StoreId = "s1", SaveId = "save-0001", Changes = new DesignerDiscardRequest { Page = new DesignerSavePage { GroupId = "new" } } };

        var result = await Operations().DiscardAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Contains("never been published", result.Error);
    }

    [Fact]
    public async Task DiscardAsync_DryRun_ListsTheDraftsItWouldThrowAwayAndWritesNothing()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = "g1",
            StoreId = "s1",
            Pages =
            [
                new SiteBuilderPage { Id = "p1", StoreId = "s1", Status = ModuleConstants.PageStatuses.Published },
                new SiteBuilderPage { Id = "p2", StoreId = "s1", Status = ModuleConstants.PageStatuses.Draft },
            ],
        });
        _pages.SeedContent("p2", Edited);
        _live.Files[Theme(Header)] = "live header";
        _live.Files[Theme($"{Header}-draft")] = HeaderFile;
        _live.Files[Theme($"{Rules}-draft")] = RulesFile;
        var files = new Dictionary<string, string>(_live.Files);
        var request = DiscardRequest(new DesignerSavePage { GroupId = "g1" }, Header, Rules);

        var result = await Operations().DiscardAsync(request, _user, Ct);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Collection(result.Preview.Objects,
            page => Assert.Equal((ModuleConstants.ChangePreview.Page, "g1", Edited, Published), (page.ObjectType, page.ObjectId, page.Fields.Single().From, page.Fields.Single().To)),
            header => Assert.Equal((ModuleConstants.ChangePreview.ThemeFile, Header, HeaderFile, "live header"),
                (header.ObjectType, header.ObjectId, header.Fields.Single().From, header.Fields.Single().To)));
        Assert.Equal([$"{Rules} has never been published: it keeps its draft."], result.Preview.Warnings);
        Assert.Equal(files, _live.Files);
        Assert.Equal(["p1", "p2"], (await _pages.GetByIdAsync("g1")).Pages.Select(x => x.Id));
        Assert.Empty(_live.Events);
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task DiscardAsync_DryRunOfAFilePage_KeepsItsDraft()
    {
        _live.Files[Page("about.page")] = "live about";
        _live.Files[Page("about.page-draft")] = "draft about";

        var result = await Operations().DiscardAsync(DiscardRequest(new DesignerSavePage { Path = "about.page", Type = "pages" }), _user, Ct);

        var change = Assert.Single(result.Preview.Objects);
        Assert.Equal((ModuleConstants.ChangePreview.FilePage, "about.page", "draft about", "live about"),
            (change.ObjectType, change.ObjectId, change.Fields.Single().From, change.Fields.Single().To));
        Assert.Equal("draft about", _live.Files[Page("about.page-draft")]);
        Assert.Empty(_live.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardAsync_FilePageNeverPublished_IsInvalidOnTheDryRunAsOnTheDiscard(bool dryRun)
    {
        _live.Files[Page("about.page-draft")] = "draft about";
        var request = DiscardRequest(new DesignerSavePage { Path = "about.page", Type = "pages" });
        request.DryRun = dryRun;

        var result = await Operations().DiscardAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Contains("never been published", result.Error);
        Assert.Equal("draft about", _live.Files[Page("about.page-draft")]);
    }

    [Fact]
    public async Task DiscardAsync_DryRunOfAPageNeverPublished_IsInvalidAsTheDiscardIs()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "new", StoreId = "s1", Pages = [new SiteBuilderPage { Id = "d1", StoreId = "s1", Status = ModuleConstants.PageStatuses.Draft }] });

        var result = await Operations().DiscardAsync(DiscardRequest(new DesignerSavePage { GroupId = "new" }), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Contains("never been published", result.Error);
        Assert.Single((await _pages.GetByIdAsync("new")).Pages);
    }

    [Fact]
    public async Task DiscardAsync_DryRun_TellsTheVersionOfEachDraft()
    {
        SeedDrafts();

        var result = await Operations().DiscardAsync(DiscardRequest(new DesignerSavePage { GroupId = "g1" }, Header), _user, Ct);

        var pageETag = (await _pages.LoadGroupContentAsync(await _pages.GetByIdAsync("g1"), cancellationToken: Ct)).ETag;
        Assert.Equal([pageETag, ETagOf(Header)], result.Preview.Objects.Select(x => x.ETag));
    }

    [Fact]
    public async Task DiscardAsync_TheReviewedDrafts_AreDiscarded()
    {
        SeedDrafts();
        var request = await ReviewedAsync(DiscardRequest(new DesignerSavePage { GroupId = "g1" }, Header));

        var result = await Operations().DiscardAsync(request, _user, Ct);

        Assert.True(result.Succeeded);
        Assert.False(_live.Files.ContainsKey(Theme($"{Header}-draft")));
        Assert.Equal(["p1"], (await _pages.GetByIdAsync("g1")).Pages.Select(x => x.Id));
    }

    [Fact]
    public async Task DiscardAsync_ThemeFileChangedSinceTheReview_IsAConflictAndDiscardsNothing()
    {
        SeedDrafts();
        var request = await ReviewedAsync(DiscardRequest(new DesignerSavePage { GroupId = "g1" }, Header));
        _live.Files[Theme($"{Header}-draft")] = RulesFile;

        var result = await Operations().DiscardAsync(request, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Conflict, SiteBuilderContentConflictException.DraftChangedMessage), (result.Status, result.Error));
        Assert.Equal(RulesFile, _live.Files[Theme($"{Header}-draft")]);
        Assert.Equal(["p1", "p2"], (await _pages.GetByIdAsync("g1")).Pages.Select(x => x.Id));
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task DiscardAsync_ThemeFileDraftedSinceTheReview_IsAConflict()
    {
        SeedDrafts();
        var request = await ReviewedAsync(DiscardRequest(new DesignerSavePage { GroupId = "g1" }, Header, Rules));
        _live.Files[Theme(Rules)] = "live rules";
        _live.Files[Theme($"{Rules}-draft")] = RulesFile;

        var result = await Operations().DiscardAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.Equal(HeaderFile, _live.Files[Theme($"{Header}-draft")]);
    }

    [Fact]
    public async Task DiscardAsync_PageChangedSinceTheReview_IsAConflictAndPutsTheThemeDraftsBack()
    {
        SeedDrafts();
        var request = await ReviewedAsync(DiscardRequest(new DesignerSavePage { GroupId = "g1" }, Header));
        _pages.SeedContent("p2", Published);

        var result = await Operations().DiscardAsync(request, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Conflict, SiteBuilderContentConflictException.PageChangedMessage), (result.Status, result.Error));
        Assert.Equal(HeaderFile, _live.Files[Theme($"{Header}-draft")]);
        Assert.Equal(["p1", "p2"], (await _pages.GetByIdAsync("g1")).Pages.Select(x => x.Id));
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task DiscardAsync_FilePage_DiscardsTheReviewedDraftAndRefusesAChangedOne()
    {
        _live.Files[Page("about.page")] = "live about";
        _live.Files[Page("about.page-draft")] = "draft about";
        var request = await ReviewedAsync(DiscardRequest(new DesignerSavePage { Path = "about.page", Type = "pages" }));
        _live.Files[Page("about.page-draft")] = "draft about, edited";

        var changed = await Operations().DiscardAsync(request, _user, Ct);
        var reviewed = await Operations().DiscardAsync(await ReviewedAsync(DiscardRequest(new DesignerSavePage { Path = "about.page", Type = "pages" })), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, changed.Status);
        Assert.True(reviewed.Succeeded);
        Assert.False(_live.Files.ContainsKey(Page("about.page-draft")));
    }

    [Fact]
    public async Task DiscardAsync_WithoutVersions_DiscardsTheDraftsWhateverTheyHold()
    {
        SeedDrafts();
        var request = DiscardRequest(new DesignerSavePage { GroupId = "g1" }, Header);
        request.DryRun = false;

        var result = await Operations().DiscardAsync(request, _user, Ct);

        Assert.True(result.Succeeded);
        Assert.False(_live.Files.ContainsKey(Theme($"{Header}-draft")));
        Assert.Equal(["p1"], (await _pages.GetByIdAsync("g1")).Pages.Select(x => x.Id));
    }

    /// <summary>The discard of what a dry run of <paramref name="request"/> lists, at the versions it tells.</summary>
    private async Task<DesignerDiscardOperationRequest> ReviewedAsync(DesignerDiscardOperationRequest request)
    {
        var dryRun = await Operations().DiscardAsync(request, _user, Ct);
        var page = dryRun.Preview.Objects.SingleOrDefault(x => x.ObjectType != ModuleConstants.ChangePreview.ThemeFile);

        request.DryRun = false;
        request.ExpectedPageVersion = page?.ETag;
        request.ExpectedThemeFileVersions = dryRun.Preview.Objects
            .Where(x => x.ObjectType == ModuleConstants.ChangePreview.ThemeFile)
            .ToDictionary(x => x.ObjectId, x => x.ETag);

        return request;
    }

    private void SeedDrafts()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = "g1",
            StoreId = "s1",
            Pages =
            [
                new SiteBuilderPage { Id = "p1", StoreId = "s1", Status = ModuleConstants.PageStatuses.Published },
                new SiteBuilderPage { Id = "p2", StoreId = "s1", Status = ModuleConstants.PageStatuses.Draft },
            ],
        });
        _pages.SeedContent("p2", Edited);
        _live.Files[Theme(Header)] = "live header";
        _live.Files[Theme($"{Header}-draft")] = HeaderFile;
    }

    [Fact]
    public async Task ReadThemeFileAsync_StoreUserDoesNotWorkIn_IsForbiddenBeforeThePathIsChecked()
    {
        var request = new DesignerThemeFileRequest { StoreId = "s1", Path = "default/../x.json" };

        var result = await Operations(new FakeAuthorizationService("s2", Permissions.Read)).ReadThemeFileAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task ReadThemeFileAsync_WhatCouldNotBeRead_IsInvalid()
    {
        var request = new DesignerThemeFileRequest { StoreId = "s1", Path = Header, BindingErrors = [NotRead] };

        var result = await Operations().ReadThemeFileAsync(request, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Invalid, NotRead), (result.Status, result.Error));
    }

    [Fact]
    public async Task ReadThemeFileAsync_File_AnswersItsDraftAtItsVersion()
    {
        _live.Files[Theme(Header)] = "live header";
        _live.Files[Theme($"{Header}-draft")] = HeaderFile;

        var result = await Operations().ReadThemeFileAsync(new DesignerThemeFileRequest { StoreId = "s1", Path = Header }, _user, Ct);

        Assert.Equal((Header, HeaderFile, ETagOf(Header)), (result.Value.Path, result.Value.Content, result.Value.ETag));
    }

    private DesignerOperationService Operations(IAuthorizationService authorization = null)
    {
        authorization ??= new FakeAuthorizationService(true);
        var options = Options.Create(new GitContentOptions());
        var policy = GitHubConnections.CreatePolicy(options, new FakeSettingsManager(false), new FakeGitContentOwnership());
        var filePageDrafts = TestServices.FilePageDrafts(_live, options, GitHubConnections.CreateService(options.Value), policy, null, null, new FakeGitContentProvisioner());
        var themeDrafts = new ThemeDraftService(_themes, _lock, _live, NullLogger<ThemeDraftService>.Instance);
        var themePublisher = new ThemePublisher(_themes, new ThemeSnapshotBuilder(), new ThemeValidator(), _live.StoreService, new SchemaCatalogService(_themes),
            _lock, _live, new NoContentHistory(), Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, new SiteBuilderContentGuard(), new ThemePaletteService());

        return TestServices.DesignerOperations(
            authorizationService: authorization,
            contentPermissionService: TestServices.Permissions(authorization),
            requestValidator: RequestValidators.Content("s1"),
            designerContentService: TestServices.Designer(_history, filePageDrafts, themeDrafts, _pages, _live, themePublisher, null, new SchemaCatalogService(_themes)),
            themeDraftService: themeDrafts,
            groupedPageService: _pages,
            sharedComponentPlacementValidator: new SiteBuilderSharedComponentPlacementValidator(new NoopSharedComponentReferenceIndexService(), authorization));
    }

    private static DesignerDiscardOperationRequest DiscardRequest(DesignerSavePage page, params string[] themeFiles) =>
        new() { StoreId = "s1", SaveId = "save-0001", Changes = new DesignerDiscardRequest { Page = page, ThemeFiles = themeFiles }, DryRun = true };

    private static DesignerSaveOperationRequest SaveRequest(DesignerSaveRequest changes, string catalogHash = Catalog) =>
        new() { StoreId = "s1", Changes = changes, CatalogHash = catalogHash, SaveId = "save-0001" };

    private DesignerSaveRequest Save(DesignerSavePage page, params (string Path, string Content)[] themeFiles) => new()
    {
        Page = page,
        ThemeFiles = themeFiles.Select(x => new DesignerSaveFile { Path = x.Path, Content = x.Content, ETag = ETagOf(x.Path) }).ToList(),
    };

    private DesignerSavePage DatabasePage() => new()
    {
        GroupId = "g1",
        ETag = SiteBuilderContentVersion.Create(_pages.GetByIdAsync("g1").GetAwaiter().GetResult(), Published),
        Content = Edited,
    };

    private void AssertNothingWritten()
    {
        Assert.DoesNotContain(_live.Files.Keys, x => x.EndsWith("-draft"));
        Assert.Equal(Published, _pages.LoadGroupContentAsync(_pages.GetByIdAsync("g1").GetAwaiter().GetResult(), cancellationToken: Ct).GetAwaiter().GetResult().Content);
        Assert.Empty(_history.Records);
    }

    private static string Theme(string path) => $"themes/s1/{path}";

    private static string Page(string path) => $"pages/s1/{path}";

    private string ETagOf(string path) =>
        SiteBuilderContentVersion.CreateForThemeFile("s1", path, _live.Files.GetValueOrDefault(Theme($"{path}-draft")) ?? _live.Files.GetValueOrDefault(Theme(path)));

    private Task<string> SaveCatalogAsync() =>
        new SchemaCatalogService(_themes).SaveAsync("s1",
            $$"""{ "hash": "{{Catalog}}", "sections": { "site-header": {}, "text": {} }, "blocks": {}, "shared": {} }""", Ct);

    private Task<string> SaveContentCatalogAsync() =>
        new SchemaCatalogService(_themes).SaveAsync("s1",
            $$"""
            {
              "hash": "{{Catalog}}",
              "sections": {
                "text": {},
                "notice": {
                  "settings": [
                    { "id": "body", "type": "text" },
                    { "id": "note", "type": "markdown", "resultType": "html", "translatable": true }
                  ]
                }
              },
              "blocks": {},
              "shared": {}
            }
            """, Ct);
}
