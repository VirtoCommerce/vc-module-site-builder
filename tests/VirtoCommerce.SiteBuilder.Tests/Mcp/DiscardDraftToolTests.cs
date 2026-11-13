using System;
using System.Linq;
using System.Threading.Tasks;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.MCP;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class DiscardDraftToolTests : IAsyncDisposable
{
    private const string Tool = "discard_draft";

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;

    public DiscardDraftToolTests()
    {
        _pages = _context.UsePageStore();
        _context.SeedPage("edited", StoreA, published: true, draft: true);
        _context.SeedPage("new", StoreA, published: false, draft: true);
        _context.SeedPage("b1", StoreB, published: true, draft: true);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Editor() => McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update);

    private static McpTestUser StoreAEditor() =>
        McpTestUser.Create("store-a-editor").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update);

    [Fact]
    public void TheTool_NeedsBuilderReadAndUpdate()
    {
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), Editor()));
        Assert.False(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), Reader()));
    }

    [Fact]
    public async Task AConfirmedDiscard_DropsTheDraft_AndShowsWhatItUndid()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { kind = "page", page_id = "edited" });

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        Assert.True(result.AskedForConfirmation);
        Assert.Contains(change.Changes, x => x.Field == "section hero-banner 'hero': title" && x.From?.ToString() == "Summer" && x.To?.ToString() == "Spring");
        Assert.Equal(["Published"], (await _pages.GetByIdAsync("edited")).Pages.Select(x => x.Status));
    }

    [Fact]
    public async Task ADeclinedDiscard_DiscardsNothing()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { kind = "page", page_id = "edited" }, new McpToolCallOptions { DeclineConfirmation = true });

        Assert.Equal(McpToolCallOutcome.Declined, result.Outcome);
        Assert.Contains((await _pages.GetByIdAsync("edited")).Pages, x => x.Status == "Draft");
    }

    [Fact]
    public async Task ADryRun_DoesNotAsk_AndDiscardsNothing()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { kind = "page", page_id = "edited", dryRun = true });

        Assert.False(result.AskedForConfirmation);
        Assert.True(result.EnsureSucceeded().Read<SiteBuilderChange>().DryRun);
        Assert.Contains((await _pages.GetByIdAsync("edited")).Pages, x => x.Status == "Draft");
    }

    [Fact]
    public async Task ADraftNeverPublished_IsNotDiscarded()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { kind = "page", page_id = "new" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("never published", result.Error);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("new")).Status);
    }

    [Fact]
    public async Task APageOfAnotherStore_IsForbidden()
    {
        var result = await _context.CallAsync(StoreAEditor(), Tool, new { kind = "page", page_id = "b1" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Contains((await _pages.GetByIdAsync("b1")).Pages, x => x.Status == "Draft");
    }

    [Fact]
    public async Task AnotherKind_IsRefused()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { kind = "theme_file", page_id = "edited" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains((await _pages.GetByIdAsync("edited")).Pages, x => x.Status == "Draft");
    }
}
