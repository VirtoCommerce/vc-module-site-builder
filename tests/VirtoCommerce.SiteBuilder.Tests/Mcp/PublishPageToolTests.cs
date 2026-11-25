using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.MCP.Services;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class PublishPageToolTests : IAsyncDisposable
{
    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;

    public PublishPageToolTests()
    {
        _pages = _context.UsePageStore();
        _context.SeedPage("new", StoreA, published: false, draft: true);
        _context.SeedPage("live", StoreA, published: true, draft: false);
        _context.SeedPage("edited", StoreA, published: true, draft: true);
        _context.SeedPage("b1", StoreB, published: false, draft: true);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Approver() => McpTestUser.Create("approver").WithPermissions(Permissions.Read, Permissions.Publish);

    private static Dictionary<string, object> Page(string pageId) => new() { ["page_id"] = pageId };

    [Theory]
    [InlineData("publish_page")]
    [InlineData("unpublish_page")]
    public void TheTools_NeedBuilderReadAndPublish(string tool)
    {
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(tool), Publisher()));
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(tool), StoreAPublisher()));
        Assert.False(_context.Harness.IsVisible(_context.Harness.GetTool(tool), McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update)));
    }

    [Theory]
    [InlineData("publish_page", "new")]
    [InlineData("unpublish_page", "live")]
    public async Task ARealCallWithoutTheReviewedVersions_IsRefused_AndChangesNothing(string tool, string pageId)
    {
        var result = await _context.CallAsync(Publisher(), tool, Page(pageId));
        var approved = await _context.CallAsync(Approver(), tool, Page(pageId), Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("expected_versions", result.Error, StringComparison.Ordinal);
        Assert.Equal(McpToolCallOutcome.Failed, approved.Outcome);
        Assert.Equal(pageId == "new" ? "Draft" : "Published", (await _pages.GetByIdAsync(pageId)).Status);
    }

    [Fact]
    public async Task ADryRun_AnswersTheVersionOfTheDraft_AndOfItsSettings()
    {
        var review = (await _context.CallAsync(Publisher(), "publish_page", new { page_id = "new", dryRun = true })).EnsureSucceeded().Read<SiteBuilderChange>();

        Assert.True(review.DryRun);
        Assert.False(string.IsNullOrEmpty(Assert.Single(review.ExpectedVersions, x => x.Key == "new").Value));
        Assert.Equal(SiteBuilderContentVersion.CreateForGroupSettings(await _pages.GetByIdAsync("new")), review.ExpectedVersions["settings:new"]);
        Assert.Null(review.DraftOwner);
    }

    [Fact]
    public async Task ADryRun_WarnsOfACustomSectionTypeTheThemeHoldsOnlyAsADraft()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        _context.SeedDraft("stats", StoreA, """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats", "title": "Our year" } ] }""");

        var review = (await _context.CallAsync(Publisher(), "publish_page", new { page_id = "stats", dryRun = true })).EnsureSucceeded().Read<SiteBuilderChange>();

        var warning = Assert.Single(review.Warnings, x => x.Contains("(custom-stats)", StringComparison.Ordinal));
        Assert.Contains("only as a draft", warning, StringComparison.Ordinal);
        Assert.Contains("publish_theme", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADryRunOfLanguageVersions_WarnsOfACustomSectionTypeTheThemeHoldsOnlyAsADraft(bool allLanguages)
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        SeedMultilanguagePage();
        SetUpPublishAll([("ml-fr", "v-fr")], []);
        _pages.SeedContent("ml-fr-draft", """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats", "title": "Notre année" } ] }""");
        var arguments = new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["dryRun"] = true };
        arguments[allLanguages ? "all_languages" : "languages"] = allLanguages ? true : new[] { "fr-FR" };

        var review = (await _context.CallAsync(Publisher(), "publish_page", arguments)).EnsureSucceeded().Read<SiteBuilderChange>();

        var warning = Assert.Single(review.Warnings, x => x.Contains("(custom-stats)", StringComparison.Ordinal));
        Assert.Contains("only as a draft", warning, StringComparison.Ordinal);
        Assert.Contains("publish_theme", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADryRunOfAPageWithoutCustomSectionTypes_HasNoSectionTypeWarning()
    {
        await _context.SeedThemeAsync(StoreA);

        var review = (await _context.CallAsync(Publisher(), "publish_page", new { page_id = "new", dryRun = true })).EnsureSucceeded().Read<SiteBuilderChange>();

        Assert.DoesNotContain(review.Warnings, x => x.Contains("custom section type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APublishOfTheReviewedDraft_WaitsForApproval_AndPublishesNothing()
    {
        var call = await _context.ReviewAsync(Publisher(), "publish_page", Page("new"));

        var result = await _context.CallAsync(Publisher(), "publish_page", call);

        Assert.Equal(McpToolCallOutcome.PendingApproval, result.Outcome);
        Assert.Contains(result.Read<SiteBuilderChange>().Changes, x => x.Field == "status" && x.From?.ToString() == "Draft" && x.To?.ToString() == "Published");
        Assert.Equal("Draft", (await _pages.GetByIdAsync("new")).Status);
    }

    [Fact]
    public async Task AnApprovedPublish_PublishesTheReviewedDraft()
    {
        var call = await _context.ReviewAsync(Publisher(), "publish_page", Page("edited"));

        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.True(result.EnsureSucceeded().Read<SiteBuilderChange>().AffectsPublished);
        Assert.Equal(["Published"], (await _pages.GetByIdAsync("edited")).Pages.Select(x => x.Status));
        Assert.Single(_context.SavedVersions.Records);
    }

    [Fact]
    public async Task ADraftEditedBetweenRequestAndApproval_IsNotPublished()
    {
        var call = await _context.ReviewAsync(Publisher(), "publish_page", Page("edited"));
        _pages.SeedContent("edited-draft", SampleContent.Replace("Spring", "Autumn", StringComparison.Ordinal));

        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error, StringComparison.Ordinal);
        Assert.Contains((await _pages.GetByIdAsync("edited")).Pages, x => x.Status == "Draft");
        Assert.Empty(_context.SavedVersions.Records);
    }

    [Fact]
    public async Task APageWhoseAudienceWidenedBetweenRequestAndApproval_IsNotPublished()
    {
        _pages.ChangeGroup("new", x => x.OrganizationId = "org-a");
        var call = await _context.ReviewAsync(Publisher(), "publish_page", Page("new"));
        _pages.ChangeGroup("new", x => x.OrganizationId = null);

        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error, StringComparison.Ordinal);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("new")).Status);
        Assert.Empty(_context.SavedVersions.Records);
    }

    [Theory]
    [InlineData("publish_page", true)]
    [InlineData("unpublish_page", false)]
    public async Task TheWrite_SendsTheReviewedVersionToTheOperation(string tool, bool publish)
    {
        var operations = new Mock<IGroupedPageOperationService>();
        operations
            .Setup(x => x.PublishAsync(It.IsAny<GroupedPagePublishOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupedPagePublishOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun(Preview("status", "Draft", "Published", objectId: "edited", affectsPublished: true, eTag: "reviewed"))
                : SiteBuilderOperationResult.Success(affectsPublished: true));
        _context.GroupedPageOperations = operations.Object;

        var settings = SiteBuilderContentVersion.CreateForGroupSettings(await _pages.GetByIdAsync("edited"));

        var call = await _context.ReviewAsync(Publisher(), tool, Page("edited"));
        (await _context.CallAsync(Approver(), tool, call, Approved)).EnsureSucceeded();

        operations.Verify(x => x.PublishAsync(It.Is<GroupedPagePublishOperationRequest>(r => !r.DryRun && r.Publish == publish &&
            r.ExpectedDraftVersion == (publish ? "reviewed" : null) && r.ExpectedPublishedVersion == (publish ? null : "reviewed") &&
            r.ExpectedSettingsVersion == (publish ? settings : null)),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnUnpublish_WaitsForApproval_AndIsRefusedWhileTheDraftExists()
    {
        var held = await _context.CallAsync(Publisher(), "unpublish_page", await _context.ReviewAsync(Publisher(), "unpublish_page", Page("live")));
        var refused = await _context.CallAsync(Publisher(), "unpublish_page",
            new { page_id = "edited", expected_versions = new Dictionary<string, string> { ["edited"] = "any" } }, Approved);

        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.Equal("Published", (await _pages.GetByIdAsync("live")).Status);
        Assert.Equal(McpToolCallOutcome.Failed, refused.Outcome);
        Assert.Contains("discard_draft", refused.Error);
    }

    [Fact]
    public async Task AnApprovedUnpublish_TakesThePageOff_UnlessItChangedSinceTheReview()
    {
        _context.SeedPage("live-2", StoreA, published: true, draft: false);
        var call = await _context.ReviewAsync(Publisher(), "unpublish_page", Page("live"));
        var changed = await _context.ReviewAsync(Publisher(), "unpublish_page", Page("live-2"));
        _pages.SeedContent("live-2-published", SampleContent.Replace("Spring", "Autumn", StringComparison.Ordinal));

        var result = await _context.CallAsync(Approver(), "unpublish_page", call, Approved);
        var refused = await _context.CallAsync(Approver(), "unpublish_page", changed, Approved);

        result.EnsureSucceeded();
        Assert.Equal("Draft", (await _pages.GetByIdAsync("live")).Status);
        Assert.Equal(McpToolCallOutcome.Failed, refused.Outcome);
        Assert.Equal("Published", (await _pages.GetByIdAsync("live-2")).Status);
    }

    [Fact]
    public async Task APublishThatWouldReplaceAPublishedPage_IsRefusedOnTheDryRun_AndNamesIt()
    {
        _pages.ChangeGroup("new", x => x.Permalink = "/live");

        var result = await _context.CallAsync(Publisher(), "publish_page", new { page_id = "new", dryRun = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("page 'live' (Page live, en-US)", result.Error, StringComparison.Ordinal);
        Assert.Contains("unpublish_page", result.Error, StringComparison.Ordinal);
        Assert.Contains("unpublish_conflicting: true", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublishedPageWithADraft_IsNotReplaced_UntilItsDraftIsPublishedOrDiscarded()
    {
        _pages.ChangeGroup("new", x => x.Permalink = "/edited");

        var result = await _context.CallAsync(Publisher(), "publish_page", new { page_id = "new", unpublish_conflicting = true, dryRun = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("publish or discard the draft of page 'edited'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApprovedPublishWithUnpublishConflicting_TakesTheReviewedPageOff()
    {
        _pages.ChangeGroup("new", x => x.Permalink = "/live");

        var call = await _context.ReviewAsync(Publisher(), "publish_page", new Dictionary<string, object> { ["page_id"] = "new", ["unpublish_conflicting"] = true });
        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        var versions = (IDictionary<string, string>)call["expected_versions"];
        Assert.Equal(["live", "new", "settings:new"], versions.Keys.Order());
        Assert.False(string.IsNullOrEmpty(versions["live"]));
        Assert.Contains(result.EnsureSucceeded().Read<SiteBuilderChange>().Changes,
            x => x.Field == "page 'live': status" && x.From?.ToString() == "Published" && x.To?.ToString() == "Draft");
        Assert.Equal("Published", (await _pages.GetByIdAsync("new")).Status);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("live")).Status);
    }

    [Fact]
    public async Task APageRepublishedAfterTheReview_IsNotUnpublished()
    {
        _pages.ChangeGroup("new", x => x.Permalink = "/live");
        var call = await _context.ReviewAsync(Publisher(), "publish_page", new Dictionary<string, object> { ["page_id"] = "new", ["unpublish_conflicting"] = true });
        _pages.SeedContent("live-published", SampleContent.Replace("Spring", "Autumn", StringComparison.Ordinal));

        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("new")).Status);
        Assert.Equal("Published", (await _pages.GetByIdAsync("live")).Status);
    }

    [Fact]
    public async Task APageThatStartsConflictingAfterTheReview_IsNotUnpublished()
    {
        _pages.ChangeGroup("new", x => x.Permalink = "/live");
        _context.SeedPage("live-2", StoreA, published: true, draft: false);
        var call = await _context.ReviewAsync(Publisher(), "publish_page", new Dictionary<string, object> { ["page_id"] = "new", ["unpublish_conflicting"] = true });
        _pages.ChangeGroup("live-2", x => x.Permalink = "/live");

        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error, StringComparison.Ordinal);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("new")).Status);
        Assert.Equal("Published", (await _pages.GetByIdAsync("live")).Status);
        Assert.Equal("Published", (await _pages.GetByIdAsync("live-2")).Status);
    }

    [Theory]
    [InlineData("pages/about.page", null)]
    [InlineData(null, "ml")]
    public async Task UnpublishConflicting_GoesWithADatabasePageOrChosenLanguages(string pageId, string multilanguagePageId)
    {
        var result = await _context.CallAsync(Publisher(), "publish_page", new
        {
            page_id = pageId,
            multilanguage_page_id = multilanguagePageId,
            all_languages = multilanguagePageId != null,
            store_id = StoreA,
            unpublish_conflicting = true,
            dryRun = true,
        });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("unpublish_conflicting goes with page_id of a database page or with languages", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageOfAnotherStore_IsForbidden()
    {
        var result = await _context.CallAsync(StoreAPublisher(), "publish_page",
            new { page_id = "b1", expected_versions = new Dictionary<string, string> { ["b1"] = "any" } }, Approved);

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("b1")).Status);
    }

    [Fact]
    public async Task ChosenLanguages_ArePublishedSourceLanguageFirst_AsReviewed()
    {
        SeedMultilanguagePage();
        var arguments = new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["languages"] = new[] { "fr-FR" } };

        var missing = await _context.CallAsync(Publisher(), "publish_page",
            new { multilanguage_page_id = "ml", languages = new[] { "de-DE" }, expected_versions = new Dictionary<string, string>() }, Approved);
        var call = await _context.ReviewAsync(Publisher(), "publish_page", arguments);
        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, missing.Outcome);
        Assert.False(result.EnsureSucceeded().Read<SiteBuilderChange>().DryRun);
        Assert.Equal("Published", (await _pages.GetByIdAsync("ml-fr")).Status);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("ml-en")).Status);
    }

    [Fact]
    public async Task AnEmptyLanguage_IsRefusedWithItsReason()
    {
        SeedMultilanguagePage();

        var result = await _context.CallAsync(Publisher(), "publish_page", new { multilanguage_page_id = "ml", languages = new[] { "fr-FR", null }, dryRun = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("an empty one is not a language", result.Error);
    }

    [Fact]
    public async Task ChosenLanguages_WhoseDraftChangedSinceTheReview_AreNotPublished()
    {
        SeedMultilanguagePage();
        var call = await _context.ReviewAsync(Publisher(), "publish_page",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["languages"] = new[] { "en-US", "fr-FR" } });
        _pages.SeedContent("ml-fr-draft", SampleContent.Replace("Spring", "Printemps", StringComparison.Ordinal));

        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("ml-en")).Status);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("ml-fr")).Status);
    }

    [Fact]
    public async Task ChosenLanguages_WhoseSettingsChangedSinceTheReview_AreNotPublished()
    {
        SeedMultilanguagePage();
        var call = await _context.ReviewAsync(Publisher(), "publish_page",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["languages"] = new[] { "en-US", "fr-FR" } });
        _pages.ChangeGroup("ml-fr", x => x.UserGroups = "staff");

        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error, StringComparison.Ordinal);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("ml-en")).Status);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("ml-fr")).Status);
    }

    [Fact]
    public async Task ChosenLanguages_SendTheReviewedSettingsOfEachVersionToTheOperation()
    {
        SeedMultilanguagePage();
        var operations = new Mock<IGroupedPageOperationService>();
        operations
            .Setup(x => x.PublishAsync(It.IsAny<GroupedPagePublishOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupedPagePublishOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun(Preview("status", "Draft", "Published", objectId: request.GroupId, affectsPublished: true,
                    eTag: "reviewed"))
                : SiteBuilderOperationResult.Success(affectsPublished: true));
        _context.GroupedPageOperations = operations.Object;
        var settings = SiteBuilderContentVersion.CreateForGroupSettings(await _pages.GetByIdAsync("ml-fr"));

        var call = await _context.ReviewAsync(Publisher(), "publish_page",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["languages"] = new[] { "fr-FR" } });
        (await _context.CallAsync(Approver(), "publish_page", call, Approved)).EnsureSucceeded();

        operations.Verify(x => x.PublishAsync(It.Is<GroupedPagePublishOperationRequest>(r => !r.DryRun && r.GroupId == "ml-fr" &&
            r.ExpectedDraftVersion == "reviewed" && r.ExpectedSettingsVersion == settings),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChosenLanguages_WithUnpublishConflicting_TakeTheReviewedPageOff()
    {
        SeedMultilanguagePage();
        _pages.ChangeGroup("ml-fr", x => x.Permalink = "/live");
        var arguments = new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["languages"] = new[] { "fr-FR" } };

        var refused = await _context.CallAsync(Publisher(), "publish_page", new Dictionary<string, object>(arguments) { ["dryRun"] = true });
        var call = await _context.ReviewAsync(Publisher(), "publish_page", new Dictionary<string, object>(arguments) { ["unpublish_conflicting"] = true });
        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Contains("page 'live' (Page live, en-US)", refused.Error, StringComparison.Ordinal);
        Assert.Equal(["live", "ml-fr", "settings:ml-fr"], ((IDictionary<string, string>)call["expected_versions"]).Keys.Order());
        result.EnsureSucceeded();
        Assert.Equal("Published", (await _pages.GetByIdAsync("ml-fr")).Status);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("live")).Status);
    }

    [Fact]
    public async Task ChosenLanguages_ThatFailOnAPageTheyWouldReplace_NameIt_AndWhatWasPublished()
    {
        SeedMultilanguagePage();
        var operations = new Mock<IGroupedPageOperationService>();
        operations
            .Setup(x => x.PublishAsync(It.IsAny<GroupedPagePublishOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupedPagePublishOperationRequest request, ClaimsPrincipal _, CancellationToken _) =>
            {
                if (request.DryRun)
                {
                    var preview = Preview("status", "Draft", "Published", objectId: request.GroupId, affectsPublished: true, eTag: "reviewed");
                    if (request.GroupId == "ml-en")
                    {
                        preview.AddObject("page", "old-en", "old").AddField("status", "Published", "Draft");
                    }

                    return SiteBuilderOperationResult.DryRun(preview);
                }

                return request.GroupId == "ml-en"
                    ? SiteBuilderOperationResult.Success(affectsPublished: true)
                    : SiteBuilderOperationResult.PermalinkConflict(new SiteBuilderPermalinkConflictResult
                    {
                        Conflicts = [new SiteBuilderPermalinkUsage { GroupId = "old-fr", Name = "Old", CultureName = "fr-FR", CanUnpublish = false }],
                    });
            });
        _context.GroupedPageOperations = operations.Object;

        var call = await _context.ReviewAsync(Publisher(), "publish_page",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["languages"] = new[] { "en-US", "fr-FR" }, ["unpublish_conflicting"] = true });
        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("Publishing the fr-FR version 'ml-fr' failed", result.Error, StringComparison.Ordinal);
        Assert.Contains("publish or discard the draft of page 'old-fr' (Old, fr-FR)", result.Error, StringComparison.Ordinal);
        Assert.Contains("Published: en-US (and unpublished page 'old-en').", result.Error, StringComparison.Ordinal);
        operations.Verify(x => x.PublishAsync(It.Is<GroupedPagePublishOperationRequest>(r => !r.UnpublishConflicting),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Never);
        operations.Verify(x => x.PublishAsync(It.Is<GroupedPagePublishOperationRequest>(r => !r.DryRun && r.GroupId == "ml-en" &&
            r.ExpectedReplacedVersions.Count == 1 && r.ExpectedReplacedVersions.ContainsKey("old-en")),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
        operations.Verify(x => x.PublishAsync(It.Is<GroupedPagePublishOperationRequest>(r => !r.DryRun && r.GroupId == "ml-fr" &&
            r.ExpectedReplacedVersions.Count == 0), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AllLanguages_PublishesTheReviewedDrafts_AndReportsTheSkippedOnes()
    {
        var drafts = new List<(string Id, string ETag)> { ("ml-en", "v-en"), ("ml-fr", "v-fr") };
        SetUpPublishAll(drafts, [
            new MultilanguagePageBatchSkip { CultureName = "de-DE", Reason = MultilanguagePageBatchResult.NoDraftReason },
            new MultilanguagePageBatchSkip { CultureName = "fr-FR", Reason = MultilanguagePageBatchResult.ChangedReason },
            new MultilanguagePageBatchSkip { CultureName = "es-ES", Reason = MultilanguagePageBatchResult.PermalinkConflictReason },
        ]);
        var arguments = new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["all_languages"] = true };

        var call = await _context.ReviewAsync(Publisher(), "publish_page", arguments);
        var held = await _context.CallAsync(Publisher(), "publish_page", call);
        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        var warnings = result.EnsureSucceeded().Read<SiteBuilderChange>().Warnings;
        Assert.Contains(warnings, x => x.StartsWith("de-DE: no draft", StringComparison.Ordinal));
        Assert.Contains(warnings, x => x.StartsWith("fr-FR: its draft changed since it was reviewed", StringComparison.Ordinal));
        Assert.Contains(warnings, x => x.StartsWith("es-ES: another published page serves its permalink", StringComparison.Ordinal) &&
            x.Contains("languages: ['es-ES']", StringComparison.Ordinal));
        _context.MultilanguagePageOperations.Verify(x => x.PublishAllAsync(
            It.Is<MultilanguagePagePublishAllOperationRequest>(r => !r.DryRun && r.ExpectedDraftVersions.Count == 2 &&
                r.ExpectedDraftVersions["ml-en"] == "v-en" && r.ExpectedDraftVersions["ml-fr"] == "v-fr"),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AllLanguages_DryRun_WarnsOfAVersionItWouldSkip()
    {
        _context.SeedPage("ml-fr", StoreA, published: false, draft: true);
        _pages.ChangeGroup("ml-fr", x =>
        {
            x.CultureName = "fr-FR";
            x.Permalink = "/live";
        });
        _pages.ChangeGroup("live", x => x.CultureName = "fr-FR");
        SetUpPublishAll([("ml-en", "v-en"), ("ml-fr", "v-fr")], []);

        var review = (await _context.CallAsync(Publisher(), "publish_page", new { multilanguage_page_id = "ml", all_languages = true, dryRun = true }))
            .EnsureSucceeded().Read<SiteBuilderChange>();

        var warning = Assert.Single(review.Warnings);
        Assert.StartsWith("fr-FR: publishing every language skips this version", warning, StringComparison.Ordinal);
        Assert.Contains("page 'live' (Page live, fr-FR)", warning, StringComparison.Ordinal);
        Assert.Contains("languages: ['fr-FR']", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllLanguages_WithALanguageVersionAddedAfterTheReview_PublishesNothing()
    {
        var drafts = new List<(string Id, string ETag)> { ("ml-en", "v-en") };
        SetUpPublishAll(drafts, []);
        var call = await _context.ReviewAsync(Publisher(), "publish_page",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["all_languages"] = true });
        drafts.Add(("ml-de", "v-de"));

        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error, StringComparison.Ordinal);
        VerifyPublishedAll(Times.Never());
    }

    [Fact]
    public async Task AllLanguages_WithAVersionWhoseSettingsChangedAfterTheReview_PublishesNothing()
    {
        _context.SeedPage("ml-en", StoreA, published: false, draft: true);
        SetUpPublishAll([("ml-en", "v-en")], []);
        var call = await _context.ReviewAsync(Publisher(), "publish_page",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["all_languages"] = true });
        _pages.ChangeGroup("ml-en", x => x.EndDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Contains("settings:ml-en", ((IDictionary<string, string>)call["expected_versions"]).Keys);
        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error, StringComparison.Ordinal);
        VerifyPublishedAll(Times.Never());
    }

    [Fact]
    public async Task AllLanguages_SendsTheOperationOnlyTheVersionsOfTheDrafts()
    {
        _context.SeedPage("ml-en", StoreA, published: false, draft: true);
        SetUpPublishAll([("ml-en", "v-en")], []);
        var call = await _context.ReviewAsync(Publisher(), "publish_page",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["all_languages"] = true });

        (await _context.CallAsync(Approver(), "publish_page", call, Approved)).EnsureSucceeded();

        _context.MultilanguagePageOperations.Verify(x => x.PublishAllAsync(
            It.Is<MultilanguagePagePublishAllOperationRequest>(r => !r.DryRun && r.ExpectedDraftVersions.Count == 1 && r.ExpectedDraftVersions["ml-en"] == "v-en"),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AllLanguages_ThatTheOperationFindsChanged_PublishesNothing()
    {
        var drafts = new List<(string Id, string ETag)> { ("ml-en", "v-en") };
        SetUpPublishAll(drafts, [], conflict: true);
        var call = await _context.ReviewAsync(Publisher(), "publish_page",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml", ["all_languages"] = true });

        var result = await _context.CallAsync(Approver(), "publish_page", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error, StringComparison.Ordinal);
    }

    private void SeedMultilanguagePage()
    {
        var english = _context.SeedPage("ml-en", StoreA, published: false, draft: true);
        var french = _context.SeedPage("ml-fr", StoreA, published: false, draft: true);
        french.CultureName = "fr-FR";
        _context.AddMultilanguagePages(Root("ml", StoreA));
        _context.MultilanguagePages
            .Setup(x => x.GetLanguagesAsync("ml", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MultilanguagePageLanguage { CultureName = "en-US", IsDefault = true, Page = english },
                new MultilanguagePageLanguage { CultureName = "fr-FR", Page = french },
                new MultilanguagePageLanguage { CultureName = "de-DE", Status = MultilanguagePageLanguage.Missing },
            ]);
    }

    private void SetUpPublishAll(List<(string Id, string ETag)> drafts, List<MultilanguagePageBatchSkip> skipped, bool conflict = false)
    {
        _context.AddMultilanguagePages(Root("ml", StoreA));
        _context.MultilanguagePageOperations
            .Setup(x => x.PublishAllAsync(It.IsAny<MultilanguagePagePublishAllOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePagePublishAllOperationRequest request, ClaimsPrincipal _, CancellationToken _) =>
            {
                if (request.DryRun)
                {
                    var preview = new SiteBuilderChangePreview { AffectsPublished = true };
                    foreach (var (id, eTag) in drafts)
                    {
                        preview.AddObject("page", id, eTag).AddField("status", "Draft", "Published");
                    }

                    return SiteBuilderOperationResult.DryRun<MultilanguagePageBatchResult>(preview);
                }

                return conflict
                    ? SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.DraftChangedMessage).As<MultilanguagePageBatchResult>()
                    : SiteBuilderOperationResult.Success(new MultilanguagePageBatchResult { ProcessedCultureNames = ["en-US"], Skipped = skipped }, affectsPublished: true);
            });
    }

    private void VerifyPublishedAll(Times times) =>
        _context.MultilanguagePageOperations.Verify(x => x.PublishAllAsync(It.Is<MultilanguagePagePublishAllOperationRequest>(r => !r.DryRun),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), times);
}
