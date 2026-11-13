using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
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
    public async Task APageWithoutADraft_IsBlocked_AndNothingChanges()
    {
        var readiness = await CheckAsync(Reader(), new { page_id = "live" });

        Assert.False(readiness.Ready);
        Assert.Contains(readiness.Findings, x => x.Id == "no-draft" && x.Severity == McpFindingSeverities.Critical);
        Assert.Equal("Published", (await _pages.GetByIdAsync("live")).Status);
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
    public async Task TheThemeOfAnotherStore_IsRefused()
    {
        var result = await _context.CallAsync(StoreAReader(), Tool, new { theme = true, store_id = StoreB });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(WrongTargets))]
    public async Task OneTarget_IsNeeded(object arguments)
    {
        var result = await _context.CallAsync(Reader(), Tool, arguments);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
    }

    public static IEnumerable<object[]> WrongTargets() =>
    [
        [new { }],
        [new { page_id = "new", theme = true, store_id = StoreA }],
        [new { theme = true, store_id = StoreA, unpublish = true }],
    ];
}
