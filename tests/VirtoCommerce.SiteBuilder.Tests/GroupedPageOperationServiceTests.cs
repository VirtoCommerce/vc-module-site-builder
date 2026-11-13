using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using Xunit;
using Fakes = VirtoCommerce.SiteBuilder.Tests.PublishedRenameContentPreservationTests;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Tests;

public class GroupedPageOperationServiceTests
{
    private const string PublishedContent = """{"settings":{"name":"Home"},"content":[]}""";
    private const string DraftContent = """{"settings":{"name":"Home"},"content":[{"id":"a","type":"text"}]}""";
    private static readonly string TooLongName = new('a', 1025);

    private const string EditedContent = """{"settings":{"name":"Home"},"content":[{"id":"b","type":"text"}]}""";

    private readonly Fakes.FakeGroupedPageService _pages = new();
    private readonly RecordingContentHistory _history = new();
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

    public GroupedPageOperationServiceTests()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = "live", StoreId = "s1", Name = "Home", Permalink = "/home", CultureName = "en-US",
            Pages = [new SiteBuilderPage { Id = "live-published", StoreId = "s1", Status = Published }],
        });
        _pages.SeedContent("live-published", PublishedContent);
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = "draft-only", StoreId = "s1", Name = "Sale", Permalink = "/sale", CultureName = "en-US",
            Pages = [new SiteBuilderPage { Id = "draft-only-draft", StoreId = "s1", Status = Draft }],
        });
        _pages.SeedContent("draft-only-draft", DraftContent);
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = "edited", StoreId = "s1", Name = "About", Permalink = "/about", CultureName = "en-US",
            Pages =
            [
                new SiteBuilderPage { Id = "edited-published", StoreId = "s1", Status = Published },
                new SiteBuilderPage { Id = "edited-draft", StoreId = "s1", Status = Draft },
            ],
        });
        _pages.SeedContent("edited-published", PublishedContent);
        _pages.SeedContent("edited-draft", DraftContent);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpdateAsync_MoveToAStoreOfAnotherUser_IsForbiddenBeforeTheUpdateIsChecked()
    {
        var model = await GetAsync("live");
        model.StoreId = "s2";
        model.Name = TooLongName;

        var result = await Operations().UpdateAsync(new GroupedPageSaveOperationRequest { Page = model }, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task UpdateAsync_InvalidNameInTheUsersStore_IsInvalid()
    {
        var model = await GetAsync("live");
        model.Name = TooLongName;

        var result = await Operations().UpdateAsync(new GroupedPageSaveOperationRequest { Page = model }, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Invalid, SiteBuilderPageName.InvalidMessage), (result.Status, result.Error));
    }

    [Fact]
    public async Task UpdateAsync_DryRunOfAPublishedPage_AffectsPublishedAndWritesNothing()
    {
        var model = await GetAsync("live");
        model.Permalink = "/start";

        var result = await Operations().UpdateAsync(new GroupedPageSaveOperationRequest { Page = model, DryRun = true }, _user, Ct);

        Assert.True(result.Succeeded);
        Assert.True(result.AffectsPublished);
        Assert.Null(result.Value);
        var change = Assert.Single(result.Preview.Objects);
        Assert.Equal((ModuleConstants.ChangePreview.Page, "live"), (change.ObjectType, change.ObjectId));
        var field = Assert.Single(change.Fields);
        Assert.Equal((ModuleConstants.ChangePreview.Permalink, "/home", "/start"), (field.Name, field.From, field.To));
        Assert.Equal("/home", (await GetAsync("live")).Permalink);
    }

    [Fact]
    public async Task UpdateAsync_DraftOnlyPage_DoesNotAffectPublished()
    {
        var model = await GetAsync("draft-only");
        model.Permalink = "/sales";

        var dryRun = await Operations().UpdateAsync(new GroupedPageSaveOperationRequest { Page = model, DryRun = true }, _user, Ct);
        var saved = await Operations().UpdateAsync(new GroupedPageSaveOperationRequest { Page = model, ReportImpact = true }, _user, Ct);

        Assert.False(dryRun.AffectsPublished);
        Assert.Equal((true, false), (saved.Succeeded, saved.AffectsPublished));
        Assert.Equal("/sales", (await GetAsync("draft-only")).Permalink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAsync_PublishedPage_SavedAndAffectsPublishedWhenAskedForTheImpact(bool reportImpact)
    {
        var model = await GetAsync("live");
        model.Permalink = "/start";

        var result = await Operations().UpdateAsync(new GroupedPageSaveOperationRequest { Page = model, ReportImpact = reportImpact }, _user, Ct);

        Assert.Equal((true, reportImpact), (result.Succeeded, result.AffectsPublished));
        Assert.Equal("/start", (await GetAsync("live")).Permalink);
    }

    [Fact]
    public async Task CreateAsync_DryRun_SavesNothing()
    {
        var model = new GroupedSiteBuilderPage { StoreId = "s1", Name = "New", Permalink = "/new", MultilanguagePageId = "root" };

        var result = await Operations().CreateAsync(new GroupedPageSaveOperationRequest { Page = model, DryRun = true }, _user, Ct);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        var change = Assert.Single(result.Preview.Objects);
        Assert.Null(change.ObjectId);
        Assert.Contains(change.Fields, x => x.Name == ModuleConstants.ChangePreview.Status && x.From == null && x.To == Draft);
        Assert.DoesNotContain(change.Fields, x => x.Name == ModuleConstants.ChangePreview.MultilanguagePageId);
        Assert.Null(model.Id);
        Assert.Empty(model.Pages);
    }

    [Fact]
    public async Task CreateWithContentAsync_MalformedContentInAStoreOfAnotherUser_IsInvalidAsTheBodyIsCheckedFirst()
    {
        var request = new GroupedPageCreateOperationRequest
        {
            Page = new CreateGroupedPageRequest { StoreId = "s2", Name = "New", Permalink = "/new", Content = "[]" },
        };

        var result = await Operations().CreateWithContentAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task SaveRawContentAsync_MalformedContentOfAPageOfAnotherUser_IsForbiddenBeforeTheContentIsChecked()
    {
        var request = new GroupedPageContentSaveOperationRequest { GroupId = "live", Content = "{ not json" };

        var result = await Operations(new FakeAuthorizationService("s2")).SaveRawContentAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task SaveContentAsync_MalformedContentOfAPageOfAnotherUser_IsInvalidAsTheDocumentIsCheckedFirst()
    {
        var request = new GroupedPageContentSaveOperationRequest { GroupId = "live", Content = "{ not json" };

        var result = await Operations(new FakeAuthorizationService("s2")).SaveContentAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task SaveContentAsync_DryRun_PreviewsOverTheDraftAndWritesNothing()
    {
        var eTag = await ETagAsync("edited");
        var request = new GroupedPageContentSaveOperationRequest { GroupId = "edited", Content = EditedContent, ETag = eTag, DryRun = true };

        var result = await Operations().SaveContentAsync(request, _user, Ct);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        var change = Assert.Single(result.Preview.Objects);
        Assert.Equal(eTag, change.ETag);
        var field = Assert.Single(change.Fields);
        Assert.Equal((ModuleConstants.ChangePreview.Content, DraftContent, EditedContent), (field.Name, field.From, field.To));
        Assert.Equal(DraftContent, await _pages.LoadContent("edited-draft", Ct));
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task SaveContentAsync_HeaderAndBodyNameOtherVersions_IsInvalid()
    {
        var request = new GroupedPageContentSaveOperationRequest
        {
            GroupId = "edited", Content = EditedContent, ETag = await ETagAsync("edited"), IfMatch = "\"other\"",
        };

        var result = await Operations().SaveContentAsync(request, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Invalid, "If-Match and the body ETag must identify the same content version."), (result.Status, result.Error));
    }

    [Fact]
    public async Task SaveContentAsync_WithoutAVersion_RequiresIt()
    {
        var request = new GroupedPageContentSaveOperationRequest { GroupId = "edited", Content = EditedContent };

        var result = await Operations().SaveContentAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.VersionRequired, result.Status);
    }

    [Fact]
    public async Task SaveContentAsync_Saved_RecordsTheHistoryUnderTheSaveId()
    {
        var request = new GroupedPageContentSaveOperationRequest
        {
            GroupId = "edited", Content = EditedContent, IfMatch = await ETagAsync("edited"), SaveId = "save-0001",
        };

        var result = await Operations().SaveContentAsync(request, _user, Ct);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        Assert.Equal(await ETagAsync("edited"), result.Value.ETag);
        Assert.Equal("save-0001", Assert.Single(_history.Records).Record.SaveId);
    }

    [Fact]
    public async Task PublishAsync_DryRun_AffectsPublishedAndPublishesNothing()
    {
        var request = new GroupedPagePublishOperationRequest { GroupId = "draft-only", Publish = true, DryRun = true };

        var result = await Operations().PublishAsync(request, _user, Ct);

        Assert.Equal((true, true), (result.Succeeded, result.AffectsPublished));
        var field = Assert.Single(Assert.Single(result.Preview.Objects).Fields);
        Assert.Equal((ModuleConstants.ChangePreview.Status, Draft, Published), (field.Name, field.From, field.To));
        Assert.Equal(Draft, (await GetAsync("draft-only")).Status);
        Assert.Empty(_history.Records);
    }

    [Fact]
    public async Task PublishAsync_DryRunWithoutADraft_IsInvalidAsThePublishIs()
    {
        var request = new GroupedPagePublishOperationRequest { GroupId = "live", Publish = true, DryRun = true };

        var result = await Operations().PublishAsync(request, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Invalid, "Draft page not found."), (result.Status, result.Error));
    }

    [Fact]
    public async Task PublishAsync_UnpublishDryRun_AffectsPublishedAndKeepsThePagePublished()
    {
        var request = new GroupedPagePublishOperationRequest { GroupId = "live", Publish = false, DryRun = true };

        var result = await Operations().PublishAsync(request, _user, Ct);

        Assert.True(result.AffectsPublished);
        Assert.Equal(Published, (await GetAsync("live")).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_Published_RecordsTheHistoryAndAffectsPublishedWhenAskedForTheImpact(bool reportImpact)
    {
        var request = new GroupedPagePublishOperationRequest { GroupId = "edited", Publish = true, SaveId = "save-0001", ReportImpact = reportImpact };

        var result = await Operations().PublishAsync(request, _user, Ct);

        Assert.Equal((true, reportImpact), (result.Succeeded, result.AffectsPublished));
        Assert.Equal(Published, (await GetAsync("edited")).Pages.Single().Status);
        Assert.Equal(("save-0001", ModuleConstants.GitHistory.PublishedSummary), (_history.Records.Single().Record.SaveId, _history.Records.Single().Record.Description));
    }

    [Fact]
    public async Task DiscardDraftAsync_DryRun_PreviewsThePublishedContentAndKeepsTheDraft()
    {
        var result = await Operations().DiscardDraftAsync(new GroupedPageDiscardOperationRequest { GroupId = "edited", DryRun = true }, _user, Ct);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        var field = Assert.Single(Assert.Single(result.Preview.Objects).Fields);
        Assert.Equal((DraftContent, PublishedContent), (field.From, field.To));
        Assert.Equal(2, (await GetAsync("edited")).Pages.Count);
    }

    [Fact]
    public async Task DiscardDraftAsync_DryRunOfANeverPublishedPage_IsInvalidAsTheDiscardIs()
    {
        var result = await Operations().DiscardDraftAsync(new GroupedPageDiscardOperationRequest { GroupId = "draft-only", DryRun = true }, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task CopyContentAsync_SourceOfAnotherUser_IsForbidden()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "foreign", StoreId = "s2", Pages = [new SiteBuilderPage { Id = "foreign-draft", StoreId = "s2", Status = Draft }] });

        var result = await Operations().CopyContentAsync(new GroupedPageContentCopyOperationRequest { GroupId = "draft-only", SourceGroupId = "foreign" }, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task CopyContentAsync_DryRun_PreviewsTheSourceOverTheDraftAndWritesNothing()
    {
        var request = new GroupedPageContentCopyOperationRequest { GroupId = "draft-only", SourceGroupId = "live", DryRun = true };

        var result = await Operations().CopyContentAsync(request, _user, Ct);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        var field = Assert.Single(Assert.Single(result.Preview.Objects).Fields);
        Assert.Equal((DraftContent, PublishedContent), (field.From, field.To));
        Assert.Equal(DraftContent, await _pages.LoadContent("draft-only-draft", Ct));
    }

    private GroupedPageOperationService Operations(IAuthorizationService authorization = null)
    {
        authorization ??= new FakeAuthorizationService("s1");
        var pageService = new Fakes.FakeSiteBuilderPageService(_pages);
        var contentService = new SiteBuilderPageContentService(pageService, _pages, new Fakes.NoopEventPublisher(),
            NullLogger<SiteBuilderPageContentService>.Instance);

        return TestServices.GroupedPageOperations(authorization, _pages, contentService, pageService,
            designerContentService: TestServices.Designer(_history, pages: _pages), contentHistory: _history);
    }

    private async Task<GroupedSiteBuilderPage> GetAsync(string groupId) => (await _pages.GetAsync([groupId])).Single();

    private async Task<string> ETagAsync(string groupId) => (await _pages.LoadGroupContentAsync(await GetAsync(groupId), cancellationToken: Ct)).ETag;
}
