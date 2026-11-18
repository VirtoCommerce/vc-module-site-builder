using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP.Services;
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
    public async Task Link_WithoutUnpublishedOnly_TheReviewedChangeWaitsForApproval_AndLinksNothing()
    {
        SetUpLink(affectsPublished: false);

        var call = await _context.ReviewAsync(Editor(), "link_page_language", LinkArguments());
        var result = await _context.CallAsync(Editor(), "link_page_language", call);

        Assert.Equal(McpToolCallOutcome.PendingApproval, result.Outcome);
        Assert.Contains(result.Read<SiteBuilderChange>().Changes, x => x.Field == "multilanguage_page_id" && x.To?.ToString() == "ml-a");
        var versions = (IDictionary<string, string>)call["expected_versions"];
        Assert.Equal((2, "landing-v1", "ml-a-v1"), (versions.Count, versions["page:landing"], versions["multilanguage_page:ml-a"]));
        VerifyNoWrite();
    }

    [Fact]
    public async Task Link_WithoutUnpublishedOnly_ACallWithoutTheReviewedVersions_IsRefused_AndNeverWaitsForApproval()
    {
        SetUpLink(affectsPublished: true);

        var result = await _context.CallAsync(Editor(), "link_page_language", LinkArguments());

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("expected_versions", result.Error);
        VerifyNoWrite();
    }

    [Fact]
    public async Task Link_Approved_IsWrittenAtTheReviewedVersions()
    {
        SetUpLink(affectsPublished: true);
        var call = await _context.ReviewAsync(Editor(), "link_page_language", LinkArguments());

        var result = await _context.CallAsync(Editor(), "link_page_language", call, Approved);

        Assert.Equal(McpToolCallOutcome.Succeeded, result.Outcome);
        Operations.Verify(x => x.LinkAsync(It.Is<MultilanguagePageLinkOperationRequest>(r => !r.DryRun && !r.RequireUnpublished &&
            r.ExpectedSettingsVersion == "landing-v1" && r.ExpectedMultilanguagePageVersion == "ml-a-v1"), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Link_Approved_AfterTheMultilanguagePageChanged_IsRefused_AndLinksNothing()
    {
        SetUpLink(affectsPublished: true);
        var call = await _context.ReviewAsync(Editor(), "link_page_language", LinkArguments());
        SetUpLink(affectsPublished: true, rootVersion: "ml-a-v2");

        var result = await _context.CallAsync(Editor(), "link_page_language", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error);
        VerifyNoWrite();
    }

    [Fact]
    public async Task Link_UnpublishedOnly_OfAnUnpublishedPage_LinksAtOnce()
    {
        SetUpLink(affectsPublished: false);

        var result = await _context.CallAsync(Editor(), "link_page_language", new { page_id = "landing", multilanguage_page_id = "ml-a", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Succeeded, result.Outcome);
        Operations.Verify(x => x.LinkAsync(It.Is<MultilanguagePageLinkOperationRequest>(r => !r.DryRun && r.GroupId == "landing" && r.MultilanguagePageId == "ml-a" &&
            r.RequireUnpublished), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Link_UnpublishedOnly_OfAPagePublishedAfterTheCheck_IsRefused_NotReported()
    {
        SetUpLink(affectsPublished: false, publishedAtTheWrite: true);

        var result = await _context.CallAsync(Editor(), "link_page_language", new { page_id = "landing", multilanguage_page_id = "ml-a", unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("was published after the check", result.Error);
        Assert.Contains("Nothing was changed", result.Error);
    }

    [Theory]
    [InlineData("convert_page_to_multilanguage", "landing")]
    [InlineData("unlink_page_language", "version")]
    public async Task ConvertAndUnlink_PassUnpublishedOnly_ToTheWrite(string tool, string pageId)
    {
        Operations
            .Setup(x => x.ConvertAsync(It.IsAny<MultilanguagePageVersionOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageVersionOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<MultilanguagePage>(Preview("name", null, "Page landing", "multilanguage-page"))
                : SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.PublishedPageMessage).As<MultilanguagePage>());
        Operations
            .Setup(x => x.UnlinkAsync(It.IsAny<MultilanguagePageVersionOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageVersionOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(Preview("multilanguagePageId", "ml-a", null, objectId: "version"))
                : SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.PublishedPageMessage).As<GroupedSiteBuilderPage>());
        var creator = McpTestUser.Create("creator").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Create);

        var result = await _context.CallAsync(creator, tool, new { page_id = pageId, unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("was published after the check", result.Error);
        Assert.All(Operations.Invocations.Where(x => x.Arguments[0] is MultilanguagePageVersionOperationRequest { DryRun: false }),
            x => Assert.True(((MultilanguagePageVersionOperationRequest)x.Arguments[0]).RequireUnpublished));
    }

    [Fact]
    public async Task Reset_UnpublishedOnly_OfAVersionPublishedAfterTheCheck_SaysWhatMayHaveBeenSaved()
    {
        Operations
            .Setup(x => x.ResetOverridesAsync(It.IsAny<MultilanguagePageResetOverridesOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageResetOverridesOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<IList<string>>(Preview("permalink", "/fr", "/ml-a", objectId: "fr"))
                : SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.PublishedPageMessage).As<IList<string>>());

        var result = await _context.CallAsync(Editor(), "reset_language_overrides", new { multilanguage_page_id = "ml-a", fields = new[] { "permalink" }, unpublished_only = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("may keep the change", result.Error);
        Operations.Verify(x => x.ResetOverridesAsync(It.Is<MultilanguagePageResetOverridesOperationRequest>(r => !r.DryRun && r.RequireUnpublished),
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
            .Setup(x => x.UnlinkAsync(It.IsAny<MultilanguagePageVersionOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageVersionOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(Versioned(Preview("multilanguagePageId", "ml-a", null, objectId: "version",
                    affectsPublished: true), "version-v1"))
                : SiteBuilderOperationResult.Success(new GroupedSiteBuilderPage { Id = "version" }, affectsPublished: true));

        var call = await _context.ReviewAsync(Editor(), "unlink_page_language", new Dictionary<string, object> { ["page_id"] = "version" });
        var held = await _context.CallAsync(Editor(), "unlink_page_language", call);
        var approved = await _context.CallAsync(Editor(), "unlink_page_language", call, Approved);

        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.True(approved.EnsureSucceeded().Read<SiteBuilderChange>().AffectsPublished);
        Operations.Verify(x => x.UnlinkAsync(It.Is<MultilanguagePageVersionOperationRequest>(r => !r.DryRun && !r.RequireUnpublished &&
            r.ExpectedSettingsVersion == "version-v1"), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unlink_Approved_OfAVersionChangedRightBeforeTheWrite_IsRefused()
    {
        Operations
            .Setup(x => x.UnlinkAsync(It.IsAny<MultilanguagePageVersionOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageVersionOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(Versioned(Preview("multilanguagePageId", "ml-a", null, objectId: "version",
                    affectsPublished: true), "version-v1"))
                : SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.SettingsChangedMessage).As<GroupedSiteBuilderPage>());
        var call = await _context.ReviewAsync(Editor(), "unlink_page_language", new Dictionary<string, object> { ["page_id"] = "version" });

        var result = await _context.CallAsync(Editor(), "unlink_page_language", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error);
    }

    [Fact]
    public async Task Convert_DryRun_WritesNothing()
    {
        Operations
            .Setup(x => x.ConvertAsync(It.IsAny<MultilanguagePageVersionOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.DryRun<MultilanguagePage>(Preview("name", null, "Page landing", "multilanguage-page")));
        var creator = McpTestUser.Create("creator").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Create);

        var result = await _context.CallAsync(creator, "convert_page_to_multilanguage", new { page_id = "landing", unpublished_only = true, dryRun = true });

        Assert.True(result.EnsureSucceeded().Read<SiteBuilderChange>().DryRun);
        Operations.Verify(x => x.ConvertAsync(It.Is<MultilanguagePageVersionOperationRequest>(r => !r.DryRun), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Never);
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
        var held = await _context.CallAsync(Editor(), "reset_language_overrides", await _context.ReviewAsync(Editor(), "reset_language_overrides",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml-a", ["fields"] = new[] { "permalink", "user_groups" } }));

        Assert.Equal(McpToolCallOutcome.Failed, unknown.Outcome);
        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.Equal([MultilanguagePageInheritance.Permalink, MultilanguagePageInheritance.UserGroups], fields);
        Assert.Contains(held.Read<SiteBuilderChange>().Changes, x => x.Field == "page 'fr': permalink");
    }

    [Fact]
    public async Task Reset_Approved_AfterTheMultilanguagePageChanged_IsRefused_AndResetsNothing()
    {
        // The published version overrides the groups, so the Multilanguage Page's own groups change at once while the reset waits.
        SetUpReset(rootVersion: "ml-a-v1");
        var call = await _context.ReviewAsync(Editor(), "reset_language_overrides",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml-a", ["fields"] = new[] { "user_groups" } });
        SetUpReset(rootVersion: "ml-a-v2");

        var result = await _context.CallAsync(Editor(), "reset_language_overrides", call, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error);
        Operations.Verify(x => x.ResetOverridesAsync(It.Is<MultilanguagePageResetOverridesOperationRequest>(r => !r.DryRun), It.IsAny<ClaimsPrincipal>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reset_Approved_IsWrittenAtTheReviewedVersions()
    {
        SetUpReset(rootVersion: "ml-a-v1");
        var call = await _context.ReviewAsync(Editor(), "reset_language_overrides",
            new Dictionary<string, object> { ["multilanguage_page_id"] = "ml-a", ["fields"] = new[] { "user_groups" } });

        var result = await _context.CallAsync(Editor(), "reset_language_overrides", call, Approved);

        Assert.Equal(McpToolCallOutcome.Succeeded, result.Outcome);
        Operations.Verify(x => x.ResetOverridesAsync(It.Is<MultilanguagePageResetOverridesOperationRequest>(r => !r.DryRun &&
            r.ExpectedMultilanguagePageVersion == "ml-a-v1" && r.ExpectedSettingsVersions.Count == 1 && r.ExpectedSettingsVersions["fr"] == "fr-v1"),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Dictionary<string, object> LinkArguments() => new() { ["page_id"] = "landing", ["multilanguage_page_id"] = "ml-a" };

    // As the operation links: with RequireUnpublished, a page published by the time of the write is refused under its lock.
    private void SetUpLink(bool affectsPublished, bool publishedAtTheWrite = false, string rootVersion = "ml-a-v1")
    {
        Operations
            .Setup(x => x.LinkAsync(It.IsAny<MultilanguagePageLinkOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageLinkOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(WithRoot(Versioned(Preview("multilanguagePageId", null, "ml-a", objectId: "landing",
                    affectsPublished: affectsPublished), "landing-v1"), rootVersion))
                : request.RequireUnpublished && publishedAtTheWrite
                    ? SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.PublishedPageMessage).As<GroupedSiteBuilderPage>()
                    : SiteBuilderOperationResult.Success(new GroupedSiteBuilderPage { Id = "landing" }, affectsPublished));
    }

    // A reset of the published French version's groups to those of ml-a, at the given version of ml-a.
    private void SetUpReset(string rootVersion)
    {
        Operations
            .Setup(x => x.ResetOverridesAsync(It.IsAny<MultilanguagePageResetOverridesOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageResetOverridesOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<IList<string>>(WithRoot(Versioned(Preview("userGroups", "VIP", "Staff", objectId: "fr", affectsPublished: true),
                    "fr-v1"), rootVersion))
                : SiteBuilderOperationResult.Success<IList<string>>(["fr-FR"], affectsPublished: true));
    }

    // As the operation answers it: the version of the page it changes.
    private static SiteBuilderChangePreview Versioned(SiteBuilderChangePreview preview, string version)
    {
        preview.Objects[0].ETag = version;
        return preview;
    }

    // As the operation answers it: the Multilanguage Page whose values the page takes, unchanged, at its version.
    private static SiteBuilderChangePreview WithRoot(SiteBuilderChangePreview preview, string version)
    {
        preview.Objects.Add(new SiteBuilderObjectChange { ObjectType = "multilanguage-page", ObjectId = "ml-a", ETag = version });
        return preview;
    }

    private void VerifyNoWrite() =>
        Operations.Verify(x => x.LinkAsync(It.Is<MultilanguagePageLinkOperationRequest>(r => !r.DryRun), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()),
            Times.Never);
}
