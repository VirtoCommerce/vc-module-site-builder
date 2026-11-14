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

    private static McpTestUser Approver() => McpTestUser.Create("approver").WithPermissions(Permissions.Read, Permissions.Update);

    private static Dictionary<string, object> Arguments(string pageId, params (string Name, object Value)[] settings)
    {
        var arguments = new Dictionary<string, object> { ["page_id"] = pageId };
        foreach (var (name, value) in settings)
        {
            arguments[name] = value;
        }

        return arguments;
    }

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
    public async Task WithoutUnpublishedOnly_TheReviewedChangeWaitsForApproval_AndChangesNothing()
    {
        var call = await _context.ReviewAsync(Editor(), Tool, Arguments("new", ("name", "Renamed")));
        var result = await _context.CallAsync(Editor(), Tool, call);

        Assert.Equal(McpToolCallOutcome.PendingApproval, result.Outcome);
        Assert.Contains(result.Read<SiteBuilderChange>().Changes, x => x.Field == "name" && x.To?.ToString() == "Renamed");
        Assert.Equal("Page new", (await _pages.GetByIdAsync("new")).Name);
    }

    [Fact]
    public async Task WithoutUnpublishedOnly_ACallWithoutTheReviewedVersion_IsRefused_AndNeverWaitsForApproval()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "live", name = "Renamed" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("expected_versions", result.Error);
        Assert.Equal("Page live", (await _pages.GetByIdAsync("live")).Name);
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
    public async Task AnApprovedChange_OfAPublishedPage_IsMade_AtTheReviewedVersion()
    {
        var reviewed = SiteBuilderContentVersion.CreateForGroupSettings(await _pages.GetByIdAsync("live"));
        var call = await _context.ReviewAsync(Editor(), Tool, Arguments("live", ("clear", new[] { "end_date" }), ("visible", false)));

        var result = await _context.CallAsync(Approver(), Tool, call, Approved);

        Assert.True(result.EnsureSucceeded().Read<SiteBuilderChange>().AffectsPublished);
        Assert.False((await _pages.GetByIdAsync("live")).Visibility);
        Assert.Equal(reviewed, ((IDictionary<string, string>)call["expected_versions"])["live"]);
    }

    [Fact]
    public async Task AnApprovedChange_OfAPageChangedSinceTheReview_IsRefused_AndKeepsTheOtherChange()
    {
        var call = await _context.ReviewAsync(Editor(), Tool, Arguments("live", ("name", "Renamed")));
        _pages.ChangeGroup("live", x => x.Visibility = false);

        var result = await _context.CallAsync(Approver(), Tool, call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error);
        var page = await _pages.GetByIdAsync("live");
        Assert.Equal(("Page live", false), (page.Name, page.Visibility));
    }

    [Fact]
    public async Task AChangeOfAnotherSettingAtTheWrite_IsNotOverwritten()
    {
        _pages.UnderLock = () => _pages.ChangeGroup("new", x => x.Permalink = "/moved");

        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "new", name = "Renamed", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error);
        var page = await _pages.GetByIdAsync("new");
        Assert.Equal(("Page new", "/moved"), (page.Name, page.Permalink));
    }

    [Fact]
    public async Task TheWrite_AppliesOnlyTheNamedSettings_ToThePageAsItIsThen()
    {
        GroupedPageSaveOperationRequest written = null;
        var operations = new Mock<IGroupedPageOperationService>();
        operations
            .Setup(x => x.UpdateAsync(It.IsAny<GroupedPageSaveOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupedPageSaveOperationRequest request, ClaimsPrincipal _, CancellationToken _) =>
            {
                if (request.DryRun)
                {
                    // a colleague's save lands between the dry run and the write
                    _pages.ChangeGroup("new", x => x.Permalink = "/moved");
                    var preview = Preview("name", "Page new", "Renamed", objectId: "new");
                    preview.Objects[0].ETag = "settings-v1";
                    return SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(preview);
                }

                written = request;
                return SiteBuilderOperationResult.Success(request.Page, affectsPublished: false);
            });
        _context.GroupedPageOperations = operations.Object;

        (await _context.CallAsync(Editor(), Tool, new { page_id = "new", name = "Renamed", unpublished_only = true })).EnsureSucceeded();

        Assert.Equal(("Renamed", "/moved"), (written.Page.Name, written.Page.Permalink));
        Assert.Equal(("settings-v1", true), (written.ExpectedSettingsVersion, written.RequireUnpublished));
    }

    [Fact]
    public async Task APageOfAnotherStore_IsForbidden()
    {
        var result = await _context.CallAsync(StoreAEditor(), Tool, new { page_id = "b1", name = "Renamed", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Equal("Page b1", (await _pages.GetByIdAsync("b1")).Name);
    }

    [Fact]
    public async Task UnpublishedOnly_OnAPagePublishedBetweenTheCheckAndTheWrite_IsRefused_AndChangesNothing()
    {
        _pages.UnderLock = () => _pages.ChangeGroup("new", x =>
        {
            if (x.Pages.All(p => p.Status != "Published"))
            {
                x.Pages.Add(new SiteBuilderPage { Id = "new-published", GroupId = "new", StoreId = StoreA, Status = "Published" });
            }
        });

        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "new", name = "Renamed", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("was published after the check", result.Error);
        Assert.Contains("Nothing was changed", result.Error);
        Assert.Equal("Page new", (await _pages.GetByIdAsync("new")).Name);
    }

    [Fact]
    public async Task TheValuesOfAMultilanguagePage_WaitForApprovalWhenAPublishedVersionInheritsThem()
    {
        _context.MultilanguagePageOperations
            .Setup(x => x.UpdateAsync(It.IsAny<MultilanguagePageSaveOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.DryRun<MultilanguagePage>(
                Preview("permalink", "/ml-a", "/about", "multilanguage-page", "ml-a", affectsPublished: true)));

        var held = await _context.CallAsync(Editor(), Tool,
            await _context.ReviewAsync(Editor(), Tool, new Dictionary<string, object> { ["multilanguage_page_id"] = "ml-a", ["permalink"] = "/about" }));
        var refused = await _context.CallAsync(Editor(), Tool, new { multilanguage_page_id = "ml-a", permalink = "/about", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.Equal(McpToolCallOutcome.Failed, refused.Outcome);
        _context.MultilanguagePageOperations.Verify(x => x.UpdateAsync(It.Is<MultilanguagePageSaveOperationRequest>(r => !r.DryRun),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheValuesOfAMultilanguagePage_AreWrittenAtTheReviewedVersion_AndRefusedWhenAVersionWasPublishedMeanwhile()
    {
        _context.MultilanguagePageOperations
            .Setup(x => x.UpdateAsync(It.IsAny<MultilanguagePageSaveOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageSaveOperationRequest request, ClaimsPrincipal _, CancellationToken _) =>
            {
                if (request.DryRun)
                {
                    var preview = Preview("permalink", "/ml-a", "/about", "multilanguage-page", "ml-a");
                    preview.Objects[0].ETag = "root-v1";
                    return SiteBuilderOperationResult.DryRun<MultilanguagePage>(preview);
                }

                return SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.PublishedPageMessage).As<MultilanguagePage>();
            });

        var result = await _context.CallAsync(Editor(), Tool, new { multilanguage_page_id = "ml-a", permalink = "/about", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("was published after the check", result.Error);
        _context.MultilanguagePageOperations.Verify(x => x.UpdateAsync(
            It.Is<MultilanguagePageSaveOperationRequest>(r => !r.DryRun && r.RequireUnpublished && r.ExpectedSettingsVersion == "root-v1" && r.Page.Permalink == "/about"),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task APageStoredAsAFile_IsRefused()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "pages/about.md", name = "About", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("database pages only", result.Error);
        Assert.Empty((await _pages.GetAsync(["pages/about.md"])).ToList());
    }
}
