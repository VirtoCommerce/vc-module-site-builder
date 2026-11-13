using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class SearchSitePagesToolTests : IAsyncDisposable
{
    private const string Tool = "search_site_pages";

    private readonly McpTestContext _context = new();
    private readonly List<GroupedSiteBuilderPage> _groups = [];
    private SiteBuilderPageSearchCriteria _lastCriteria;

    public SearchSitePagesToolTests()
    {
        _groups.AddRange(Enumerable.Range(1, 5).Select(x => Group($"a{x}", StoreA)));
        _groups.AddRange(Enumerable.Range(1, 2).Select(x => Group($"b{x}", StoreB)));
        _context.AddGroups([.. _groups]);

        // The pages list as the service answers it: the store, the page ids and the page of rows.
        _context.PageList
            .Setup(x => x.SearchAsync(It.IsAny<SiteBuilderPageSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteBuilderPageSearchCriteria criteria, CancellationToken _) =>
            {
                _lastCriteria = criteria;
                var matches = _groups
                    .Where(x => criteria.StoreId == null || x.StoreId == criteria.StoreId)
                    .Where(x => criteria.ObjectIds is not { Count: > 0 } || criteria.ObjectIds.Contains(x.Id))
                    .ToList();

                return new SiteBuilderPageListSearchResult
                {
                    TotalCount = matches.Count,
                    Results = matches.Skip(criteria.Skip).Take(criteria.Take).Select(ToListItem).ToList(),
                };
            });
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public async Task AReaderOfEveryStore_FindsThePagesOfEveryStore()
    {
        var page = await SearchAsync(Reader(), new { });

        Assert.Equal(7, page.TotalCount);
        Assert.Null(_lastCriteria.StoreId);
    }

    [Fact]
    public async Task TheFilters_ReachThePagesList()
    {
        await SearchAsync(Reader(), new { store_id = StoreA, keyword = "spring", status = "published,draft", language = "fr-FR", page_type = "multilanguage" });

        Assert.Equal(StoreA, _lastCriteria.StoreId);
        Assert.Equal("spring", _lastCriteria.Keyword);
        Assert.Equal("Published,Draft", _lastCriteria.Statuses);
        Assert.Equal("fr-FR", _lastCriteria.LanguageCode);
        Assert.Equal("Multilanguage", _lastCriteria.PageType);
    }

    [Fact]
    public async Task AnUnknownStatus_IsRefused_WithTheKnownOnes()
    {
        var result = await _context.CallAsync(Reader(), Tool, new { store_id = StoreA, status = "Live" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("Draft, Published, Archived", result.Error);
    }

    [Fact]
    public async Task AStoreReader_FindsThePagesOfTheirStore()
    {
        var page = await SearchAsync(StoreAReader(), new { store_id = StoreA });

        Assert.Equal(5, page.TotalCount);
        Assert.All(page.Items, x => Assert.Equal(StoreA, x.StoreId));
    }

    [Fact]
    public async Task AStoreReader_IsRefusedAnotherStore()
    {
        var result = await _context.CallAsync(StoreAReader(), Tool, new { store_id = StoreB });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Contains(StoreB, result.Error);
        _context.PageList.Verify(x => x.SearchAsync(It.IsAny<SiteBuilderPageSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AStoreReader_IsRefusedEveryStore_AndToldToNameOne()
    {
        var result = await _context.CallAsync(StoreAReader(), Tool, new { });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Contains("store_id", result.Error);
    }

    [Fact]
    public async Task AnAccountBoundToAStore_IsRefusedAnotherStore()
    {
        var result = await _context.CallAsync(_context.StoreBoundReader(), Tool, new { store_id = StoreB });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task AStoreReader_IsRefusedAPageOfAnotherStore()
    {
        var result = await _context.CallAsync(StoreAReader(), Tool, new { page_id = "b1" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        _context.PageContents.Verify(x => x.LoadGroupContentAsync(It.IsAny<GroupedSiteBuilderPage>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TwoPages_HoldEveryPageOnce()
    {
        var first = await SearchAsync(Reader(), new { store_id = StoreA, take = 3 });
        var second = await SearchAsync(Reader(), new { store_id = StoreA, skip = first.NextSkip, take = 3 });

        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(
            _groups.Where(x => x.StoreId == StoreA).Select(x => x.Id).Order(),
            first.Items.Concat(second.Items).Select(x => x.Id).Order());
    }

    [Fact]
    public async Task ThePeriod_ListsThePagesWithAVersionChangedInIt()
    {
        var since = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        SiteBuilderPageSearchCriteria versionCriteria = null;
        _context.PageVersionSearch
            .Setup(x => x.SearchAsync(It.IsAny<SiteBuilderPageSearchCriteria>(), It.IsAny<bool>()))
            .Callback((SiteBuilderPageSearchCriteria criteria, bool _) => versionCriteria = criteria)
            .ReturnsAsync(new SiteBuilderPageSearchResult
            {
                TotalCount = 3,
                Results =
                [
                    new SiteBuilderPage { Id = "v1", GroupId = "a2" },
                    new SiteBuilderPage { Id = "v2", GroupId = "a2" },
                    new SiteBuilderPage { Id = "v3", GroupId = "a4" },
                ],
            });

        var page = await SearchAsync(Reader(), new { store_id = StoreA, modified_since = "2026-11-10T00:00:00Z" });

        Assert.Equal(since, versionCriteria.ModifiedSince);
        Assert.Equal(StoreA, versionCriteria.StoreId);
        Assert.Equal(["a2", "a4"], page.Items.Select(x => x.Id).Order());
    }

    [Fact]
    public async Task APeriodWithoutChanges_FindsNothing()
    {
        _context.PageVersionSearch
            .Setup(x => x.SearchAsync(It.IsAny<SiteBuilderPageSearchCriteria>(), It.IsAny<bool>()))
            .ReturnsAsync(new SiteBuilderPageSearchResult { TotalCount = 0, Results = [] });

        var page = await SearchAsync(Reader(), new { store_id = StoreA, modified_before = "2020-01-01T00:00:00Z" });

        Assert.Equal(0, page.TotalCount);
        _context.PageList.Verify(x => x.SearchAsync(It.IsAny<SiteBuilderPageSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task APage_IsReadWithItsSettingsSectionsAndVersionToken()
    {
        var group = _groups.Single(x => x.Id == "a1");
        group.Pages.Add(new SiteBuilderPage { Id = "a1-draft", GroupId = "a1", StoreId = StoreA, Status = "Draft" });
        _context.PageContents
            .Setup(x => x.LoadGroupContentAsync(It.Is<GroupedSiteBuilderPage>(g => g.Id == "a1"), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SiteBuilderContentSnapshot("a1-draft",
                """{ "settings": { "title": "Spring sale" }, "content": [ { "id": "hero", "type": "hero-banner", "hidden": false } ] }""",
                "\"etag-1\""));

        var result = await _context.CallAsync(StoreAReader(), Tool, new { page_id = "a1" });
        var page = result.EnsureSucceeded().Read<SitePageDetails>();

        Assert.Equal("a1", page.Id);
        Assert.Equal("Draft", page.ContentVersion);
        Assert.True(page.HasUnpublishedChanges);
        Assert.Equal("\"etag-1\"", page.VersionToken);
        Assert.Equal("Spring sale", page.Settings["title"]!.GetValue<string>());
        Assert.Equal("hero-banner", page.Sections.Single()!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task APageInTheLegacyShape_IsReadInTheCurrentOne()
    {
        _context.PageContents
            .Setup(x => x.LoadGroupContentAsync(It.IsAny<GroupedSiteBuilderPage>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SiteBuilderContentSnapshot("a1-version", """[ { "title": "Old" }, { "type": "text" }, { "type": "image" } ]""", "\"etag\""));

        var result = await _context.CallAsync(Reader(), Tool, new { page_id = "a1", version = "published" });
        var page = result.EnsureSucceeded().Read<SitePageDetails>();

        Assert.Equal("Published", page.ContentVersion);
        Assert.Equal("Old", page.Settings["title"]!.GetValue<string>());
        Assert.Equal(["text", "image"], page.Sections.Select(x => x!["type"]!.GetValue<string>()));
    }

    [Fact]
    public async Task APageOfAnotherStoreThanTheOneNamed_IsNotFoundThere()
    {
        var result = await _context.CallAsync(Reader(), Tool, new { store_id = StoreA, page_id = "b1" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("is not a page of store", result.Error);
    }

    [Fact]
    public async Task AnUnknownPage_IsNotFound()
    {
        var result = await _context.CallAsync(Reader(), Tool, new { page_id = "missing" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("was not found", result.Error);
    }

    private async Task<McpPage<SitePageRow>> SearchAsync(McpTestUser user, object arguments)
    {
        var result = await _context.CallAsync(user, Tool, arguments);

        return result.EnsureSucceeded().Read<McpPage<SitePageRow>>();
    }

    private static SiteBuilderPageListItem ToListItem(GroupedSiteBuilderPage group) => new()
    {
        Type = "Landing",
        Id = group.Id,
        Name = group.Name,
        Permalink = group.Permalink,
        CreatedDate = group.CreatedDate,
        Page = group,
    };
}
