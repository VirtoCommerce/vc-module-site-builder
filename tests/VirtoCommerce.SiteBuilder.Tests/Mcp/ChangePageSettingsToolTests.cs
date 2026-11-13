using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.MCP;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.CreatePageToolTests;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class ChangePageSettingsToolTests : IAsyncDisposable
{
    private const string Tool = "change_page_settings";

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;

    public ChangePageSettingsToolTests()
    {
        _pages = _context.UsePageStore();
        _context.SeedPage("new", StoreA, published: false, draft: true);
        _context.SeedPage("live", StoreA, published: true, draft: false);
        _context.SeedPage("b1", StoreB, published: false, draft: true);
        _context.AddMultilanguagePages(Root("ml-a", StoreA));
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Editor() => McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update);

    private static McpTestUser StoreAEditor() =>
        McpTestUser.Create("store-a-editor").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update);

    [Fact]
    public void TheTool_NeedsBuilderReadAndUpdate()
    {
        var tool = _context.Harness.GetTool(Tool);

        Assert.True(_context.Harness.IsVisible(tool, Editor()));
        Assert.True(_context.Harness.IsVisible(tool, StoreAEditor()));
        Assert.False(_context.Harness.IsVisible(tool, Reader()));
    }

    [Fact]
    public async Task WithoutUnpublishedOnly_TheChangeWaitsForApproval_AndChangesNothing()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "new", name = "Renamed" });

        Assert.Equal(McpToolCallOutcome.PendingApproval, result.Outcome);
        Assert.Contains(result.Read<SiteBuilderChange>().Changes, x => x.Field == "name" && x.To?.ToString() == "Renamed");
        Assert.Equal("Page new", (await _pages.GetByIdAsync("new")).Name);
    }

    [Fact]
    public async Task UnpublishedOnly_ChangesAPageThatWasNeverPublished_AtOnce()
    {
        var result = await _context.CallAsync(Editor(), Tool, new
        {
            page_id = "new",
            name = "Renamed",
            user_groups = new[] { "b2b", "vip" },
            end_date = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            unpublished_only = true,
        });

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        Assert.Equal(McpToolCallOutcome.Succeeded, result.Outcome);
        Assert.False(change.AffectsPublished);
        var page = await _pages.GetByIdAsync("new");
        Assert.Equal(("Renamed", "b2b,vip", new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)), (page.Name, page.UserGroups, page.EndDate));
    }

    [Fact]
    public async Task UnpublishedOnly_OnAPublishedPage_IsRefusedAndChangesNothing()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "live", name = "Renamed", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("without unpublished_only", result.Error);
        Assert.Equal("Page live", (await _pages.GetByIdAsync("live")).Name);
    }

    [Fact]
    public async Task UnpublishedOnly_DryRunOnAPublishedPage_SaysTheCallWouldBeRefused()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "live", visible = false, unpublished_only = true, dryRun = true });

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        Assert.True(change.AffectsPublished);
        Assert.Contains(change.Warnings, x => x.Contains("refused"));
        Assert.True((await _pages.GetByIdAsync("live")).Visibility);
    }

    [Fact]
    public async Task AnApprovedChange_OfAPublishedPage_IsMade()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "live", clear = new[] { "end_date" }, visible = false },
            new McpToolCallOptions { AsApprovedChange = true });

        Assert.True(result.EnsureSucceeded().Read<SiteBuilderChange>().AffectsPublished);
        Assert.False((await _pages.GetByIdAsync("live")).Visibility);
    }

    [Fact]
    public async Task APageOfAnotherStore_IsForbidden()
    {
        var result = await _context.CallAsync(StoreAEditor(), Tool, new { page_id = "b1", name = "Renamed", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Equal("Page b1", (await _pages.GetByIdAsync("b1")).Name);
    }

    [Fact]
    public async Task APagePublishedBetweenTheCheckAndTheWrite_IsReported()
    {
        var operations = new Mock<IGroupedPageOperationService>();
        operations
            .Setup(x => x.UpdateAsync(It.IsAny<GroupedPageSaveOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupedPageSaveOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(Preview("name", "Page new", "Renamed", objectId: "new"))
                : SiteBuilderOperationResult.Success(request.Page, affectsPublished: true));
        _context.GroupedPageOperations = operations.Object;

        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "new", name = "Renamed", unpublished_only = true });

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        Assert.True(change.ChangedPublishedWithoutApproval);
        Assert.Contains(change.Warnings, x => x.Contains("was not undone"));
    }

    [Fact]
    public async Task TheValuesOfAMultilanguagePage_WaitForApprovalWhenAPublishedVersionInheritsThem()
    {
        _context.MultilanguagePageOperations
            .Setup(x => x.UpdateAsync(It.IsAny<MultilanguagePageSaveOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.DryRun<MultilanguagePage>(
                Preview("permalink", "/ml-a", "/about", "multilanguage-page", "ml-a", affectsPublished: true)));

        var held = await _context.CallAsync(Editor(), Tool, new { multilanguage_page_id = "ml-a", permalink = "/about" });
        var refused = await _context.CallAsync(Editor(), Tool, new { multilanguage_page_id = "ml-a", permalink = "/about", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.Equal(McpToolCallOutcome.Failed, refused.Outcome);
        _context.MultilanguagePageOperations.Verify(x => x.UpdateAsync(It.Is<MultilanguagePageSaveOperationRequest>(r => !r.DryRun),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AValueAndItsClear_AreRefused()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "new", end_date = DateTime.UtcNow, clear = new[] { "end_date" }, unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("not both", result.Error);
        Assert.Null((await _pages.GetByIdAsync("new")).EndDate);
    }

    [Fact]
    public async Task APageStoredAsAFile_IsNotAvailableYet()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "pages/about.md", name = "About", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("not available for pages stored as files yet", result.Error);
        Assert.Empty((await _pages.GetAsync(["pages/about.md"])).ToList());
    }
}
