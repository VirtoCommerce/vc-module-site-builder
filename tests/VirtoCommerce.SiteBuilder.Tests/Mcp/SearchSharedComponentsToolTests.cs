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
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class SearchSharedComponentsToolTests : IAsyncDisposable
{
    private const string Tool = "search_shared_components";

    private readonly McpTestContext _context = new();
    private readonly List<SiteBuilderSharedComponent> _components =
    [
        .. Enumerable.Range(1, 5).Select(x => Component($"a{x}", StoreA)),
        Component("b1", StoreB),
    ];

    private bool? _lastIncludePages;

    public SearchSharedComponentsToolTests()
    {
        _context.SharedComponentSearch
            .Setup(x => x.SearchAsync(It.IsAny<SiteBuilderSharedComponentSearchCriteria>(), It.IsAny<bool>()))
            .ReturnsAsync((SiteBuilderSharedComponentSearchCriteria criteria, bool _) =>
            {
                var matches = _components.Where(x => x.StoreId == criteria.StoreId).ToList();
                return new SiteBuilderSharedComponentSearchResult
                {
                    TotalCount = matches.Count,
                    Results = matches.Skip(criteria.Skip).Take(criteria.Take).ToList(),
                };
            });
        _context.SharedComponents
            .Setup(x => x.GetAsync(It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IList<string> ids, string _, bool _) => _components.Where(x => ids.Contains(x.Id)).ToList());
        _context.SharedComponentUsages
            .Setup(x => x.GetUsageAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> ids, string _, bool includePages, CancellationToken _) =>
            {
                _lastIncludePages = includePages;
                return ids.Select(id => new SiteBuilderSharedComponentUsage
                {
                    SharedComponentId = id,
                    UsageCount = 3,
                    Pages = includePages ? [new SiteBuilderSharedComponentUsagePage { Id = "page-1", Name = "Home" }] : [],
                }).ToList();
            });
        _context.SharedComponentContents
            .Setup(x => x.TryLoadContentAsync(It.IsAny<SiteBuilderSharedComponent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{ "settings": {}, "content": [ { "id": "s1", "type": "announcement-bar" } ] }""");
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public void TheTool_NeedsSharedComponentsRead_AsWellAsBuilderRead()
    {
        Assert.DoesNotContain(Tool, _context.Harness.GetVisibleToolNames(Reader()));
        Assert.Contains(Tool, _context.Harness.GetVisibleToolNames(StoreAComponentReader()));
    }

    [Fact]
    public async Task TheComponentsOfTheStore_AreListedWithTheirUsage_WithoutPages()
    {
        var page = await SearchAsync(StoreAComponentReader(), new { store_id = StoreA });

        Assert.Equal(5, page.TotalCount);
        Assert.All(page.Items, x => Assert.Equal(3, x.UsageCount));
        Assert.False(_lastIncludePages);
    }

    [Fact]
    public async Task AStoreReader_IsRefusedAnotherStore()
    {
        var result = await _context.CallAsync(StoreAComponentReader(), Tool, new { store_id = StoreB });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Contains(Permissions.SharedComponentsRead, result.Error);
    }

    [Fact]
    public async Task AComponentOfAnotherStore_IsNotFoundInTheStoreNamed()
    {
        var result = await _context.CallAsync(StoreAComponentReader(), Tool, new { store_id = StoreA, shared_component_id = "b1" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("was not found in store", result.Error);
        _context.SharedComponentContents.Verify(x => x.TryLoadContentAsync(It.IsAny<SiteBuilderSharedComponent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AComponent_IsReadWithItsContent_AndThePagesItStandsOn()
    {
        var result = await _context.CallAsync(StoreAComponentReader(), Tool, new { store_id = StoreA, shared_component_id = "a2" });
        var component = result.EnsureSucceeded().Read<SharedComponentDetails>();

        Assert.Equal("announcement-bar", component.Sections.Single()!["type"]!.GetValue<string>());
        Assert.Equal("Home", component.UsagePages.Single().Name);
        Assert.Null(component.UsagePagesNote);
    }

    [Fact]
    public async Task WithoutBuilderReadInTheStore_ThePagesAreNotNamed()
    {
        // builder:read in store B only: the tool is visible, the pages of store A are not.
        var user = McpTestUser.Create("components-only")
            .WithScopedPermission(Permissions.SharedComponentsRead, StoreScope, StoreA)
            .WithScopedPermission(Permissions.Read, StoreScope, StoreB);

        var result = await _context.CallAsync(user, Tool, new { store_id = StoreA, shared_component_id = "a2" });
        var component = result.EnsureSucceeded().Read<SharedComponentDetails>();

        Assert.Equal(3, component.UsageCount);
        Assert.Null(component.UsagePages);
        Assert.Contains(Permissions.Read, component.UsagePagesNote);
        Assert.False(_lastIncludePages);
    }

    [Fact]
    public async Task TwoPages_HoldEveryComponentOnce()
    {
        var first = await SearchAsync(StoreAComponentReader(), new { store_id = StoreA, take = 3 });
        var second = await SearchAsync(StoreAComponentReader(), new { store_id = StoreA, skip = first.NextSkip, take = 3 });

        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(["a1", "a2", "a3", "a4", "a5"], first.Items.Concat(second.Items).Select(x => x.Id).Order());
    }

    private static McpTestUser StoreAComponentReader() => McpTestUser.Create("store-a-components")
        .WithScopedPermission(Permissions.Read, StoreScope, StoreA)
        .WithScopedPermission(Permissions.SharedComponentsRead, StoreScope, StoreA);

    private static SiteBuilderSharedComponent Component(string id, string storeId) => new()
    {
        Id = id,
        StoreId = storeId,
        Name = $"Component {id}",
        CreatedDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private async Task<McpPage<SharedComponentRow>> SearchAsync(McpTestUser user, object arguments)
    {
        var result = await _context.CallAsync(user, Tool, arguments);

        return result.EnsureSucceeded().Read<McpPage<SharedComponentRow>>();
    }
}
