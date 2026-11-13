using System;
using System.Threading.Tasks;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.MCP;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class SiteBuilderToolsetTests : IAsyncDisposable
{
    private readonly McpTestContext _context = new();

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public void Toolset_FollowsTheConventions()
    {
        McpToolsetConventions.Verify<McpAssemblyMarker>(x => x
            .Ignore(McpConventionRules.Paging, "list_versions", "The git history pages by cursor: the answer carries next_cursor, and a skip would read every older version first."));
    }

    [Fact]
    public void AReaderOfEveryPart_SeesEveryReadTool()
    {
        var user = McpTestUser.Create("designer").WithPermissions(Permissions.Read, Permissions.SharedComponentsRead, AssetRead);

        Assert.Equal(
            ["list_versions", "search_assets", "search_shared_components", "search_site_pages"],
            _context.Harness.GetVisibleToolNames(user));
    }

    [Fact]
    public void BuilderRead_ShowsThePageAndHistoryTools_TheOthersNeedTheirOwnPermission()
    {
        Assert.Equal(["list_versions", "search_site_pages"], _context.Harness.GetVisibleToolNames(Reader()));
    }

    [Fact]
    public void BuilderRead_LimitedToAStore_ShowsTheSameTools()
    {
        Assert.Equal(["list_versions", "search_site_pages"], _context.Harness.GetVisibleToolNames(StoreAReader()));
    }

    [Fact]
    public void WithoutBuilderRead_NoToolIsVisible()
    {
        var user = McpTestUser.Create("assets-only").WithPermissions(Permissions.SharedComponentsRead, AssetRead, Permissions.Update);

        Assert.Empty(_context.Harness.GetVisibleToolNames(user));
    }
}
