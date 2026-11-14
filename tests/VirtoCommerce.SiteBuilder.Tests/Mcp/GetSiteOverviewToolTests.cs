using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class GetSiteOverviewToolTests : IAsyncDisposable
{
    private const string Tool = "get_site_overview";

    private readonly McpTestContext _context = new();
    private readonly List<SiteBuilderPageListItem> _rows = [];

    public GetSiteOverviewToolTests()
    {
        var draft = Group("draft-page", StoreA, status: "Draft");
        draft.ModifiedBy = "anna";
        _rows.Add(LandingRow(draft));
        _rows.Add(LandingRow(Group("live-page", StoreA)));
        _rows.Add(new SiteBuilderPageListItem { Type = "Multilanguage", Id = "root", Name = "Root", MultilanguagePage = Root("root", StoreA) });
        _rows.Add(LandingRow(Group("b-page", StoreB)));

        // The pages list as its filters answer: store, page type, lifecycle (drafts) and translation status.
        _context.PageList
            .Setup(x => x.SearchAsync(It.IsAny<SiteBuilderPageSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteBuilderPageSearchCriteria criteria, CancellationToken _) =>
            {
                var matches = _rows
                    .Where(x => criteria.StoreId == null || (x.Page?.StoreId ?? x.MultilanguagePage?.StoreId) == criteria.StoreId)
                    .Where(x => criteria.PageType == null || x.Type == criteria.PageType)
                    .Where(x => criteria.Lifecycle != "drafts" || x.Page?.Status == "Draft")
                    .Where(x => criteria.TranslationStatus == null || (criteria.TranslationStatus == "Missing" && x.Type == "Multilanguage"))
                    .ToList();

                return new SiteBuilderPageListSearchResult { TotalCount = matches.Count, Results = matches.Take(criteria.Take).ToList() };
            });

        _context.PublishStatuses
            .Setup(x => x.GetFilePageStatusAsync(It.IsAny<SiteBuilderFilePageQueryRequest>(), It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.Success(new PagePublishStatus { Flow = "blob" }));
        _context.History.Setup(x => x.GetStatusAsync(StoreA, It.IsAny<CancellationToken>())).ReturnsAsync(ContentHistoryStatus.SetupRequired);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private async Task<McpPage<StoreOverview>> OverviewAsync(McpTestUser user, object arguments) =>
        (await _context.CallAsync(user, Tool, arguments)).EnsureSucceeded().Read<McpPage<StoreOverview>>();

    [Fact]
    public void BuilderRead_ShowsTheTool()
    {
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), StoreAReader()));
        Assert.False(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), McpTestUser.Create("none").WithPermissions(AssetRead)));
    }

    [Fact]
    public async Task AStore_GetsItsPagesThemeAndStates_WithWhatEachCounts()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "sections/header-group.json", """{ "type": "header", "name": "Header", "content": [] }""", draft: true);

        var overview = Assert.Single((await OverviewAsync(Reader(), new { store_id = StoreA })).Items);

        Assert.Equal(3, overview.Pages.Total);
        Assert.Equal(2, overview.Pages.LandingPages);
        Assert.Equal(1, overview.Pages.MultilanguagePages);
        Assert.Equal(1, overview.Pages.WithDrafts);
        Assert.Equal("draft-page", Assert.Single(overview.WaitingForPublication).Id);
        Assert.Equal("anna", overview.WaitingForPublication[0].ChangedBy);
        Assert.Equal(1, Assert.Single(overview.LanguageCoverage).Missing);
        Assert.NotEmpty(overview.Notes);

        Assert.True(overview.Theme.Available);
        Assert.Equal(1, overview.Theme.Templates);
        Assert.Equal("draft_never_published", overview.Theme.Parts.Single(x => x.Part == "header").State);
        Assert.Equal("storefront_default", overview.Theme.Parts.Single(x => x.Part == "footer").State);

        Assert.True(overview.SchemaCatalog.Available);
        Assert.False(overview.History.Available);
        Assert.Contains("not set up", overview.History.Note, StringComparison.Ordinal);
        Assert.Equal("blob", overview.Publishing.State);
    }

    [Fact]
    public async Task AStoreWithoutTheCatalog_SaysItIsNotAvailable_AndWhy()
    {
        var overview = Assert.Single((await OverviewAsync(Reader(), new { store_id = StoreB })).Items);

        Assert.False(overview.SchemaCatalog.Available);
        Assert.StartsWith("Not available:", overview.SchemaCatalog.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepositoryThatFails_IsDescribedByTheKindOfFailure_NotByItsMessage()
    {
        _context.PublishStatuses
            .Setup(x => x.GetFilePageStatusAsync(It.IsAny<SiteBuilderFilePageQueryRequest>(), It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitContentException("\"acme/secret-site\" is not a repository this platform may use. List it in SiteBuilder:GitContent:AllowedRepositories.",
                GitContentFailureKind.NotConfigured));

        var overview = Assert.Single((await OverviewAsync(Reader(), new { store_id = StoreA })).Items);

        Assert.False(overview.Publishing.Available);
        Assert.Equal($"Not available. {GitContentFailures.NotConfigured}", overview.Publishing.Note);
    }

    [Fact]
    public async Task AReaderOfStoreA_IsRefusedStoreB()
    {
        var result = await _context.CallAsync(StoreAReader(), Tool, new { store_id = StoreB });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task WithoutAStore_AReaderOfStoreA_GetsStoreAOnly_InTheShortForm()
    {
        var page = await OverviewAsync(StoreAReader(), new { });

        var overview = Assert.Single(page.Items);
        Assert.Equal(StoreA, overview.StoreId);
        Assert.Null(overview.RecentChanges);
        Assert.Null(overview.LanguageCoverage);
        Assert.Contains(overview.Notes, x => x.StartsWith("Short form", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAStore_AReaderOfEveryStore_PagesThroughTheStores_EachOnce()
    {
        var stores = new[] { StoreA, StoreB, StoreC };
        _context.StoreSearch
            .Setup(x => x.SearchAsync(It.IsAny<StoreSearchCriteria>(), It.IsAny<bool>()))
            .ReturnsAsync((StoreSearchCriteria criteria, bool _) => new StoreSearchResult
            {
                TotalCount = stores.Length,
                Results = stores.Skip(criteria.Skip).Take(criteria.Take).Select(x => new Store { Id = x, Name = x }).ToList(),
            });

        var first = await OverviewAsync(Reader(), new { take = 2 });
        var second = await OverviewAsync(Reader(), new { skip = first.NextSkip, take = 2 });

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(stores, first.Items.Concat(second.Items).Select(x => x.StoreId));
        Assert.False(second.HasMore);
    }
}
