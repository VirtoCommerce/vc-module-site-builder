using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class ListVersionsToolTests : IAsyncDisposable
{
    private const string Tool = "list_versions";

    private readonly McpTestContext _context = new();
    private readonly List<ContentVersion> _versions = Enumerable.Range(1, 5)
        .Select(x => new ContentVersion
        {
            VersionId = $"{x:D7}",
            Date = new DateTime(2026, 11, x, 0, 0, 0, DateTimeKind.Utc),
            Author = new ContentVersionAuthor { Name = "Anna", Login = "anna" },
            Description = $"Saved {x}",
            Items = ["page:a1"],
        })
        .ToList();

    private IList<HistoryItem> _lastItems;

    public ListVersionsToolTests()
    {
        _context.AddGroups(Group("a1", StoreA), Group("b1", StoreB));

        // The history pages by an opaque cursor: here the index of the next version.
        _context.History
            .Setup(x => x.ListAsync(It.IsAny<string>(), It.IsAny<IList<HistoryItem>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IList<HistoryItem> items, string cursor, int take, CancellationToken _) =>
            {
                _lastItems = items;
                var start = cursor == null ? 0 : int.Parse(cursor.TrimStart('c'));
                var versions = _versions.Skip(start).Take(take).ToList();
                return new ContentVersionPage
                {
                    Status = ContentHistoryStatus.Enabled,
                    Versions = versions,
                    Cursor = start + versions.Count < _versions.Count ? $"c{start + versions.Count}" : null,
                };
            });
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public async Task TheVersionsOfAPage_AreListed_WithTheirAuthor()
    {
        var answer = await ListAsync(StoreAReader(), new { store_id = StoreA, page_id = "a1" });

        Assert.True(answer.HistoryAvailable);
        Assert.Equal(5, answer.Versions.Count);
        Assert.Equal("Anna", answer.Versions[0].Author);
        Assert.Equal([new HistoryItem(HistoryItemType.Page, "a1")], _lastItems);
    }

    [Fact]
    public async Task ThemeFiles_AreAskedForByTheirPath()
    {
        await ListAsync(Reader(), new { store_id = StoreA, theme_files = new[] { "/default/templates/product.json" } });

        Assert.Equal([new HistoryItem(HistoryItemType.Theme, "default/templates/product.json")], _lastItems);
    }

    [Theory]
    [InlineData(ContentHistoryStatus.SetupRequired, "setup_required")]
    [InlineData(ContentHistoryStatus.Unavailable, "unavailable")]
    public async Task AStoreWithoutHistory_SaysSo(ContentHistoryStatus status, string history)
    {
        _context.History
            .Setup(x => x.ListAsync(It.IsAny<string>(), It.IsAny<IList<HistoryItem>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentVersionPage { Status = status });

        var answer = await ListAsync(Reader(), new { store_id = StoreA, page_id = "a1" });

        Assert.False(answer.HistoryAvailable);
        Assert.Equal(history, answer.History);
        Assert.Contains("keeps no history", answer.Note);
        Assert.Empty(answer.Versions);
    }

    [Fact]
    public async Task AStoreReader_IsRefusedAnotherStore()
    {
        var result = await _context.CallAsync(StoreAReader(), Tool, new { store_id = StoreB, page_id = "b1" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        _context.History.Verify(x => x.ListAsync(It.IsAny<string>(), It.IsAny<IList<HistoryItem>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task APageOfAnotherStore_IsRefused_InTheStoreTheUserReads()
    {
        var result = await _context.CallAsync(StoreAReader(), Tool, new { store_id = StoreA, page_id = "b1" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        _context.History.Verify(x => x.ListAsync(It.IsAny<string>(), It.IsAny<IList<HistoryItem>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnUnknownStore_IsRefusedByTheRequestCheck()
    {
        var result = await _context.CallAsync(Reader(), Tool, new { store_id = "missing", page_id = "a1" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("does not exist", result.Error);
    }

    [Fact]
    public async Task WithoutAnItem_TheCallIsRefused()
    {
        var result = await _context.CallAsync(Reader(), Tool, new { store_id = StoreA });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("page_id", result.Error);
    }

    [Fact]
    public async Task TwoPages_HoldEveryVersionOnce()
    {
        var first = await ListAsync(Reader(), new { store_id = StoreA, page_id = "a1", take = 3 });
        var second = await ListAsync(Reader(), new { store_id = StoreA, page_id = "a1", cursor = first.NextCursor, take = 3 });

        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(_versions.Select(x => x.VersionId), first.Versions.Concat(second.Versions).Select(x => x.VersionId));
    }

    private async Task<ContentVersionsAnswer> ListAsync(McpTestUser user, object arguments)
    {
        var result = await _context.CallAsync(user, Tool, arguments);

        return result.EnsureSucceeded().Read<ContentVersionsAnswer>();
    }
}
