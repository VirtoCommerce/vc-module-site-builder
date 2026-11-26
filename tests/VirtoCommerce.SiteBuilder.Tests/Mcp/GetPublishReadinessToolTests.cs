using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.MCP.Services;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class GetPublishReadinessToolTests : IAsyncDisposable
{
    private const string Tool = "get_publish_readiness";

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;

    public GetPublishReadinessToolTests()
    {
        _pages = _context.UsePageStore();
        _context.SeedPage("new", StoreA, published: false, draft: true);
        _context.SeedPage("live", StoreA, published: true, draft: false);
        _context.SeedPage("edited", StoreA, published: true, draft: true);
        _context.SeedPage("b1", StoreB, published: false, draft: true);
        _context.ListPages(() => []);
        _context.PageList
            .Setup(x => x.FindPermalinkUsageAsync(It.IsAny<PermalinkUsageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private async Task<PublishReadiness> CheckAsync(McpTestUser user, object arguments) =>
        (await _context.CallAsync(user, Tool, arguments)).EnsureSucceeded().Read<PublishReadiness>();

    [Fact]
    public void BuilderRead_ShowsTheTool()
    {
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), StoreAReader()));
        Assert.False(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), McpTestUser.Create("none").WithPermissions(AssetRead)));
    }

    [Fact]
    public async Task APageWithADraft_ListsWhatThePublicationTakes_SectionBySection()
    {
        var readiness = await CheckAsync(Reader(), new { page_id = "edited" });

        Assert.True(readiness.Ready);
        Assert.Equal("publish", readiness.Action);
        var part = Assert.Single(readiness.Parts);
        Assert.Equal("edited", part.ObjectId);
        Assert.Contains(part.Changes, x => x.Field.Contains("hero", StringComparison.Ordinal) && x.From == "Spring" && x.To == "Summer");
        Assert.Contains(readiness.Findings, x => x.Id == "unpublish-refused-while-draft" && x.Severity == McpFindingSeverities.Info);
        Assert.Contains($"'{StoreA}'", readiness.Affected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingHistory_IsANote_AndThePublicationIsStillChecked()
    {
        _context.History
            .Setup(x => x.ListAsync(It.IsAny<string>(), It.IsAny<IList<HistoryItem>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitContentException("GitHub request timed out", GitContentFailureKind.Unreachable));

        var readiness = await CheckAsync(Reader(), new { page_id = "edited" });

        Assert.True(readiness.Ready);
        Assert.Equal("edited", Assert.Single(readiness.Parts).ObjectId);
        var finding = Assert.Single(readiness.Findings, x => x.Id == "history-unavailable");
        Assert.Equal(McpFindingSeverities.Info, finding.Severity);
        Assert.Equal(GitContentFailures.Unreachable, finding.Evidence);
    }

    [Fact]
    public async Task APageWithoutADraft_IsBlocked_AndNothingChanges()
    {
        var readiness = await CheckAsync(Reader(), new { page_id = "live" });

        Assert.False(readiness.Ready);
        Assert.Contains(readiness.Findings, x => x.Id == "no-draft" && x.Severity == McpFindingSeverities.Critical);
        Assert.Equal("Published", (await _pages.GetByIdAsync("live")).Status);
    }

    [Fact]
    public async Task APageThatWouldReplaceAPublishedPage_IsBlocked_AndNamesIt()
    {
        _pages.ChangeGroup("new", x => x.Permalink = "/live");

        var readiness = await CheckAsync(Reader(), new { page_id = "new" });

        Assert.False(readiness.Ready);
        var finding = Assert.Single(readiness.Findings, x => x.Id == "permalink-conflict");
        Assert.Equal(McpFindingSeverities.Critical, finding.Severity);
        Assert.Equal("page 'live' (Page live, en-US)", finding.Evidence);
        Assert.Contains("unpublish_conflicting: true", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMultilanguagePage_WarnsOfAVersionThatPublishingEveryLanguageSkips()
    {
        _context.SeedPage("ml-en", StoreA, published: false, draft: true);
        _context.SeedPage("ml-fr", StoreA, published: false, draft: true);
        _pages.ChangeGroup("ml-fr", x =>
        {
            x.CultureName = "fr-FR";
            x.Permalink = "/live";
        });
        _pages.ChangeGroup("live", x => x.CultureName = "fr-FR");
        _context.AddMultilanguagePages(Root("ml", StoreA));
        _context.MultilanguagePageOperations
            .Setup(x => x.PublishAllAsync(It.IsAny<MultilanguagePagePublishAllOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var preview = new SiteBuilderChangePreview { AffectsPublished = true };
                preview.AddObject("page", "ml-en", "v-en").AddField("status", "Draft", "Published");
                preview.AddObject("page", "ml-fr", "v-fr").AddField("status", "Draft", "Published");
                return SiteBuilderOperationResult.DryRun<MultilanguagePageBatchResult>(preview);
            });

        var readiness = await CheckAsync(Reader(), new { multilanguage_page_id = "ml" });

        Assert.True(readiness.Ready);
        Assert.Equal(["ml-en"], readiness.Parts.Select(x => x.ObjectId));
        var finding = Assert.Single(readiness.Findings, x => x.Id == "permalink-conflict");
        Assert.Equal(McpFindingSeverities.Warning, finding.Severity);
        Assert.Equal("page 'live' (Page live, fr-FR)", finding.Evidence);
        Assert.Contains("languages: ['fr-FR']", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnpublish_OfAPageWithADraft_IsBlockedByTheRule()
    {
        var readiness = await CheckAsync(Reader(), new { page_id = "edited", unpublish = true });

        Assert.False(readiness.Ready);
        Assert.Contains(readiness.Findings, x => x.Id == "unpublish-refused-while-draft" && x.Severity == McpFindingSeverities.Critical);
    }

    [Fact]
    public async Task AHiddenPage_AndAnAddressAnotherPageServes_AreWarnings()
    {
        var group = await _pages.GetByIdAsync("new");
        group.Visibility = false;
        _pages.SeedGroup(group);
        _context.PageList
            .Setup(x => x.FindPermalinkUsageAsync(It.IsAny<PermalinkUsageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PermalinkUsage { GroupId = "other", Name = "Other", CultureName = "en-US" }]);

        var readiness = await CheckAsync(Reader(), new { page_id = "new" });

        Assert.True(readiness.Ready);
        Assert.Contains(readiness.Findings, x => x.Id == "page-hidden" && x.Severity == McpFindingSeverities.Warning);
        Assert.Contains(readiness.Findings, x => x.Id == "permalink-shared" && x.Evidence.Contains("other", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APageOfAnotherStore_IsRefused()
    {
        var result = await _context.CallAsync(StoreAReader(), Tool, new { page_id = "b1" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task TheTheme_WithoutTheSchemaCatalog_IsBlocked_WithTheReason()
    {
        var readiness = await CheckAsync(Reader(), new { theme = true, store_id = StoreB });

        Assert.False(readiness.Ready);
        Assert.Contains(readiness.Findings, x => x.Id == "schema-catalog-missing" && x.Severity == McpFindingSeverities.Critical);
    }

    [Fact]
    public async Task TheTheme_WhoseCustomSectionTypesAreTooLargeTogether_IsBlocked_WithTheLimit()
    {
        await _context.SeedThemeAsync(StoreA);
        var type = System.Text.Json.Nodes.JsonNode.Parse(StatsSectionType)!;
        type["css"] = $"/* {new string('x', 60 * 1024)} */";
        foreach (var name in new[] { "custom-a", "custom-b", "custom-c", "custom-d", "custom-e" })
        {
            _context.SeedThemeFile(StoreA, $"custom-sections/{name}.json", type.ToJsonString(), draft: true);
        }

        var readiness = await CheckAsync(Reader(), new { theme = true, store_id = StoreA });

        Assert.False(readiness.Ready);
        var finding = Assert.Single(readiness.Findings, x => x.Id == "theme-refused");
        Assert.Equal(McpFindingSeverities.Critical, finding.Severity);
        Assert.Contains("of JSON together, more than 256 KB", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTheme_ListsItsDrafts()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "templates/product.bicycles.json", ProductTemplate, draft: true);

        var readiness = await CheckAsync(Reader(), new { theme = true, store_id = StoreA });

        Assert.True(readiness.Ready);
        Assert.Equal(["default/templates/product.bicycles.json"], readiness.Parts.Select(x => x.ObjectId));
        Assert.False(readiness.YouCanPublish);
    }

    [Fact]
    public async Task TheTheme_WithoutDrafts_NamesTheToolsThatMakeThem()
    {
        await _context.SeedThemeAsync(StoreA);

        var readiness = await CheckAsync(Reader(), new { theme = true, store_id = StoreA });

        var finding = Assert.Single(readiness.Findings, x => x.Id == "nothing-to-publish");
        Assert.Contains("create_section_type and change_section_type", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTheme_WithoutDrafts_WarnsOfALiveCustomSectionTypeWhoseFileIsGone_AsTheDryRunOfItsPublicationDoes()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        var first = (await _context.CallAsync(Publisher(), "publish_theme", new { store_id = StoreA, dryRun = true })).EnsureSucceeded().Read<ThemePublication>();
        (await _context.CallAsync(Publisher(), "publish_theme", new { store_id = StoreA, expected_versions = first.ExpectedVersions, expected_catalog_hash = first.CatalogHash },
            Approved)).EnsureSucceeded();
        _context.Blobs.Files.Remove(ThemeFileKey(StoreA, "custom-sections/custom-stats.json"));
        var spring = _context.SeedPage("spring", StoreA, published: true, draft: false,
            content: """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""");
        _context.ListPages(() => [LandingRow(spring)]);

        var readiness = await CheckAsync(Reader(), new { theme = true, store_id = StoreA });
        var publication = (await _context.CallAsync(Publisher(), "publish_theme", new { store_id = StoreA, dryRun = true })).EnsureSucceeded().Read<ThemePublication>();

        var finding = Assert.Single(readiness.Findings, x => x.Id == "section-type-removed");
        Assert.Equal(McpFindingSeverities.Warning, finding.Severity);
        Assert.Equal("Section type 'Stats' (custom-stats) will be removed: it is live now, but its file is no longer in the theme, so this publication " +
            "leaves it out. 1 page places it and will show nothing in its place: 'Page spring' (/spring).", finding.Title);
        Assert.Contains(finding.Title, publication.Warnings);
        var part = Assert.Single(readiness.Parts);
        Assert.Equal("default/custom-sections/custom-stats.json", part.ObjectId);
        Assert.Equal([("status", "published", "removed")], part.Changes.Select(x => (x.Field, x.From, x.To)));
        Assert.DoesNotContain(readiness.Findings, x => x.Id == "nothing-to-publish");
        Assert.True(readiness.Ready);
    }

    [Fact]
    public void TheTool_NamesTheCustomSectionTypeFindings_AndTheThemesCustomSectionTypeDrafts()
    {
        var tool = _context.Harness.GetTool("get_publish_readiness").ProtocolTool;

        Assert.Contains("a custom section type the theme does not hold, holds only as a draft or holds with unpublished changes", tool.Description,
            StringComparison.Ordinal);
        Assert.Contains("palette and custom section types",
            tool.InputSchema.GetProperty("properties").GetProperty("theme").GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageThatPlacesACustomSectionTypeOnlyInDraft_IsWarned_ToPublishTheThemeFirst()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        _context.SeedDraft("stats", StoreA, """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats", "title": "Our year" } ] }""");

        var readiness = await CheckAsync(Reader(), new { page_id = "stats" });

        Assert.True(readiness.Ready);
        var finding = Assert.Single(readiness.Findings, x => x.Id == "section-type-unpublished");
        Assert.Equal(McpFindingSeverities.Warning, finding.Severity);
        Assert.Contains("(custom-stats)", finding.Title, StringComparison.Ordinal);
        Assert.Contains("publish_theme", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilePageThatPlacesACustomSectionTypeOnlyInDraft_IsWarned_ToPublishTheThemeFirst()
    {
        const string path = "landing/spring.page";
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        _context.SeedFilePage(StoreA, path, """{ "settings": { "name": "Spring" }, "content": [] }""");
        _context.SeedFilePage(StoreA, path, """{ "settings": { "name": "Spring" }, "content": [ { "id": "s1", "type": "custom-stats" } ] }""", draft: true);
        var preview = new SiteBuilderChangePreview { AffectsPublished = true };
        preview.AddObject("file-page", path).AddField("content", "Draft", "Published");
        _context.FilePageOperations
            .Setup(x => x.PublishAsync(It.Is<FilePagePublishOperationRequest>(r => r.DryRun), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.DryRun<FilePageShipment>(preview));

        var readiness = await CheckAsync(Reader(), new { page_id = "pages/" + path, store_id = StoreA });

        var finding = Assert.Single(readiness.Findings, x => x.Id == "section-type-unpublished");
        Assert.Contains("(custom-stats)", finding.Title, StringComparison.Ordinal);
        Assert.Contains("publish_theme", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageThatPlacesAPublishedCustomSectionType_IsNotWarned()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType);
        _context.SeedDraft("stats", StoreA, """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats", "title": "Our year" } ] }""");

        var readiness = await CheckAsync(Reader(), new { page_id = "stats" });

        Assert.DoesNotContain(readiness.Findings, x => x.Id.StartsWith("section-type-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APageThatPlacesAPublishedCustomSectionTypeWithAChangedDraft_IsWarned_ThatItShowsThePublishedVersion()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType.Replace("\"Stats\"", "\"Stats v2\"", StringComparison.Ordinal), draft: true);
        _context.SeedDraft("stats", StoreA, """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats", "title": "Our year" } ] }""");

        var readiness = await CheckAsync(Reader(), new { page_id = "stats" });

        Assert.True(readiness.Ready);
        var finding = Assert.Single(readiness.Findings, x => x.Id == "section-type-changed");
        Assert.Equal(McpFindingSeverities.Warning, finding.Severity);
        Assert.Contains("(custom-stats)", finding.Title, StringComparison.Ordinal);
        Assert.Contains("published version", finding.Title, StringComparison.Ordinal);
        Assert.Contains("publish_theme", finding.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(readiness.Findings, x => x.Id == "section-type-unpublished");
    }

    [Fact]
    public async Task APageNameInAFinding_IsQuotedOnOneLine()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.PageStore.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = "stats",
            StoreId = StoreA,
            Name = "Home' is ready.\u2028Note 'x",
            Permalink = "/stats",
            CultureName = "en-US",
            Pages = [new SiteBuilderPage { Id = "stats-draft", GroupId = "stats", StoreId = StoreA, Status = "Draft" }],
        });
        _context.PageStore.SeedContent("stats-draft", """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""");

        var readiness = await CheckAsync(Reader(), new { page_id = "stats" });

        Assert.StartsWith("Page 'Home'' is ready. Note ''x' places custom section types the theme does not hold",
            Assert.Single(readiness.Findings, x => x.Id == "section-type-missing").Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageThatPlacesACustomSectionTypeTheThemeDoesNotHold_IsWarned()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedDraft("stats", StoreA, """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats", "title": "Our year" } ] }""");

        var readiness = await CheckAsync(Reader(), new { page_id = "stats" });

        var finding = Assert.Single(readiness.Findings, x => x.Id == "section-type-missing");
        Assert.Equal(McpFindingSeverities.Warning, finding.Severity);
        Assert.Contains("(custom-stats)", finding.Title, StringComparison.Ordinal);
        Assert.Contains("create_section_type", finding.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(readiness.Findings, x => x.Id == "section-type-unpublished");
    }

    [Fact]
    public async Task TheThemeOfAnotherStore_IsRefused()
    {
        var result = await _context.CallAsync(StoreAReader(), Tool, new { theme = true, store_id = StoreB });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(WrongTargets))]
    public async Task OneTarget_IsNeeded(object arguments, string refusal)
    {
        var result = await _context.CallAsync(Reader(), Tool, arguments);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains(refusal, result.Error);
    }

    public static IEnumerable<object[]> WrongTargets() =>
    [
        [new { }, "Pass one of page_id"],
        [new { page_id = "new", theme = true, store_id = StoreA }, "Pass one of page_id"],
        [new { theme = true, store_id = StoreA, unpublish = true }, "unpublish goes with page_id"],
    ];
}
