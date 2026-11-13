using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.CreatePageToolTests;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class PageLanguageToolsTests : IAsyncDisposable
{
    private readonly McpTestContext _context = new();

    public PageLanguageToolsTests()
    {
        var pages = _context.UsePageStore();
        _context.SeedPage("landing", StoreA, published: false, draft: true);
        _context.SeedPage("b1", StoreB, published: false, draft: true);
        var version = _context.SeedPage("version", StoreA, published: true, draft: false);
        version.MultilanguagePageId = "ml-a";
        pages.SeedGroup(version);
        _context.AddMultilanguagePages(Root("ml-a", StoreA), Root("ml-b", StoreB));
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private Mock<VirtoCommerce.SiteBuilder.Core.Services.IMultilanguagePageOperationService> Operations => _context.MultilanguagePageOperations;

    private static McpTestUser Editor() => McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update);

    private static McpTestUser StoreAEditor() =>
        McpTestUser.Create("store-a-editor").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update);

    [Fact]
    public void Convert_NeedsBuilderCreateToo_TheOthersBuilderUpdate()
    {
        var harness = _context.Harness;
        var creator = McpTestUser.Create("creator").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Create);

        Assert.False(harness.IsVisible(harness.GetTool("convert_page_to_multilanguage"), Editor()));
        Assert.True(harness.IsVisible(harness.GetTool("convert_page_to_multilanguage"), creator));
        Assert.All(new[] { "link_page_language", "unlink_page_language", "reset_language_overrides" },
            x => Assert.True(harness.IsVisible(harness.GetTool(x), Editor())));
        Assert.All(new[] { "link_page_language", "unlink_page_language", "reset_language_overrides" },
            x => Assert.False(harness.IsVisible(harness.GetTool(x), Reader())));
    }

    [Fact]
    public async Task Link_WithoutUnpublishedOnly_WaitsForApproval_AndLinksNothing()
    {
        SetUpLink(affectsPublished: false);

        var result = await _context.CallAsync(Editor(), "link_page_language", new { page_id = "landing", multilanguage_page_id = "ml-a" });

        Assert.Equal(McpToolCallOutcome.PendingApproval, result.Outcome);
        Assert.Contains(result.Read<SiteBuilderChange>().Changes, x => x.Field == "multilanguagePageId" && x.To?.ToString() == "ml-a");
        VerifyNoWrite();
    }

    [Fact]
    public async Task Link_UnpublishedOnly_OfAnUnpublishedPage_LinksAtOnce()
    {
        SetUpLink(affectsPublished: false);

        var result = await _context.CallAsync(Editor(), "link_page_language", new { page_id = "landing", multilanguage_page_id = "ml-a", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Succeeded, result.Outcome);
        Operations.Verify(x => x.LinkAsync(It.Is<MultilanguagePageLinkOperationRequest>(r => !r.DryRun && r.GroupId == "landing" && r.MultilanguagePageId == "ml-a"),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Link_UnpublishedOnly_TouchingAPublishedPage_IsRefused()
    {
        SetUpLink(affectsPublished: true);

        var result = await _context.CallAsync(Editor(), "link_page_language", new { page_id = "landing", multilanguage_page_id = "ml-a", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("without unpublished_only", result.Error);
        VerifyNoWrite();
    }

    [Fact]
    public async Task Link_ToAMultilanguagePageOfAnotherStore_IsForbidden()
    {
        var root = await _context.CallAsync(StoreAEditor(), "link_page_language", new { page_id = "landing", multilanguage_page_id = "ml-b", unpublished_only = true });
        var page = await _context.CallAsync(StoreAEditor(), "link_page_language", new { page_id = "b1", multilanguage_page_id = "ml-a", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Forbidden, root.Outcome);
        Assert.Equal(McpToolCallOutcome.Forbidden, page.Outcome);
        Operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unlink_OfALandingPage_IsRefused()
    {
        var result = await _context.CallAsync(Editor(), "unlink_page_language", new { page_id = "landing", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unlink_OfAPublishedVersion_RunsWhenApproved()
    {
        Operations
            .Setup(x => x.UnlinkAsync(It.IsAny<GroupedPageOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupedPageOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(Preview("multilanguagePageId", "ml-a", null, objectId: "version", affectsPublished: true))
                : SiteBuilderOperationResult.Success(new GroupedSiteBuilderPage { Id = "version" }, affectsPublished: true));

        var held = await _context.CallAsync(Editor(), "unlink_page_language", new { page_id = "version" });
        var approved = await _context.CallAsync(Editor(), "unlink_page_language", new { page_id = "version" }, new McpToolCallOptions { AsApprovedChange = true });

        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.True(approved.EnsureSucceeded().Read<SiteBuilderChange>().AffectsPublished);
    }

    [Fact]
    public async Task Convert_DryRun_WritesNothing()
    {
        Operations
            .Setup(x => x.ConvertAsync(It.IsAny<GroupedPageOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.DryRun<MultilanguagePage>(Preview("name", null, "Page landing", "multilanguage-page")));
        var creator = McpTestUser.Create("creator").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Create);

        var result = await _context.CallAsync(creator, "convert_page_to_multilanguage", new { page_id = "landing", unpublished_only = true, dryRun = true });

        Assert.True(result.EnsureSucceeded().Read<SiteBuilderChange>().DryRun);
        Operations.Verify(x => x.ConvertAsync(It.Is<GroupedPageOperationRequest>(r => !r.DryRun), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reset_TakesTheAgentsFieldNames_AndRefusesOthers()
    {
        IList<string> fields = null;
        Operations
            .Setup(x => x.ResetOverridesAsync(It.IsAny<MultilanguagePageResetOverridesOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .Callback((MultilanguagePageResetOverridesOperationRequest request, ClaimsPrincipal _, CancellationToken _) => fields = request.Fields)
            .ReturnsAsync(SiteBuilderOperationResult.DryRun<IList<string>>(Preview("permalink", "/fr", "/ml-a", objectId: "fr", affectsPublished: true)));

        var unknown = await _context.CallAsync(Editor(), "reset_language_overrides", new { multilanguage_page_id = "ml-a", fields = new[] { "name" } });
        var held = await _context.CallAsync(Editor(), "reset_language_overrides", new { multilanguage_page_id = "ml-a", fields = new[] { "permalink", "user_groups" } });

        Assert.Equal(McpToolCallOutcome.Failed, unknown.Outcome);
        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.Equal([MultilanguagePageInheritance.Permalink, MultilanguagePageInheritance.UserGroups], fields);
        Assert.Contains(held.Read<SiteBuilderChange>().Changes, x => x.Field == "page 'fr': permalink");
    }

    private void SetUpLink(bool affectsPublished)
    {
        Operations
            .Setup(x => x.LinkAsync(It.IsAny<MultilanguagePageLinkOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageLinkOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(Preview("multilanguagePageId", null, "ml-a", objectId: "landing", affectsPublished: affectsPublished))
                : SiteBuilderOperationResult.Success(new GroupedSiteBuilderPage { Id = "landing" }, affectsPublished));
    }

    private void VerifyNoWrite() =>
        Operations.Verify(x => x.LinkAsync(It.Is<MultilanguagePageLinkOperationRequest>(r => !r.DryRun), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()),
            Times.Never);
}
