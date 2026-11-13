using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class PublishThemeToolTests : IAsyncDisposable
{
    private const string Tool = "publish_theme";
    private static readonly McpToolCallOptions Approved = new() { AsApprovedChange = true };

    private readonly McpTestContext _context = new();

    public PublishThemeToolTests()
    {
        _context.UsePageStore();
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Publisher() => McpTestUser.Create("publisher").WithPermissions(Permissions.Read, Permissions.Publish);

    private static McpTestUser StoreAPublisher() =>
        McpTestUser.Create("store-a-publisher").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Publish);

    private async Task SeedAsync()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "templates/product.bicycles.json", ProductTemplate, draft: true);
    }

    private async Task<ThemePublication> DryRunAsync() =>
        (await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, dryRun = true })).EnsureSucceeded().Read<ThemePublication>();

    [Fact]
    public void TheTool_NeedsBuilderPublish()
    {
        var tool = _context.Harness.GetTool(Tool);

        Assert.True(_context.Harness.IsVisible(tool, Publisher()));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("designer").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Theme, Permissions.Templates)));
    }

    [Fact]
    public async Task TheDryRun_ListsTheDrafts_WithTheVersionsToPublish()
    {
        await SeedAsync();

        var preview = await DryRunAsync();

        Assert.True(preview.DryRun);
        Assert.Equal(["default/templates/product.bicycles.json"], preview.Drafts.Select(x => x.Path));
        Assert.Equal(["default/templates/product.bicycles.json"], preview.ExpectedVersions.Keys);
        Assert.Contains(preview.Changes, x => x.Field == "default/templates/product.bicycles.json" && x.To?.ToString() == "published");
    }

    [Fact]
    public async Task ACallWithTheVersions_WaitsForApproval_AndPublishesNothing()
    {
        await SeedAsync();
        var preview = await DryRunAsync();

        var result = await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = preview.ExpectedVersions });

        Assert.Equal(McpToolCallOutcome.PendingApproval, result.Outcome);
        Assert.Null(_context.ReadThemeFile(StoreA, "templates/product.bicycles.json"));
    }

    [Fact]
    public async Task ACallWithoutTheVersions_IsRefused_AndPublishesNothing()
    {
        await SeedAsync();

        var result = await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("expected_versions", result.Error, StringComparison.Ordinal);
        Assert.Null(_context.ReadThemeFile(StoreA, "templates/product.bicycles.json"));
    }

    [Fact]
    public async Task AnApprovedPublication_PublishesTheDraftsThatWereReviewed()
    {
        await SeedAsync();
        var preview = await DryRunAsync();

        var result = (await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = preview.ExpectedVersions }, Approved))
            .EnsureSucceeded().Read<ThemePublication>();

        Assert.False(result.DryRun);
        Assert.NotNull(result.Publication);
        Assert.Equal(ProductTemplate, _context.ReadThemeFile(StoreA, "templates/product.bicycles.json"));
        Assert.Null(_context.ReadThemeFile(StoreA, "templates/product.bicycles.json", draft: true));
    }

    [Fact]
    public async Task ADraftChangedAfterTheReview_RefusesTheApprovedPublication()
    {
        await SeedAsync();
        var preview = await DryRunAsync();
        _context.SeedThemeFile(StoreA, "templates/product.json", ProductTemplate.Replace("\"main\"", "\"main2\"", StringComparison.Ordinal), draft: true);

        var requesterCheck = await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = preview.ExpectedVersions, dryRun = true });
        var approved = await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = preview.ExpectedVersions }, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, requesterCheck.Outcome);
        Assert.Equal(McpToolCallOutcome.Failed, approved.Outcome);
        Assert.Contains("drafts changed", approved.Error, StringComparison.Ordinal);
        Assert.Null(_context.ReadThemeFile(StoreA, "templates/product.bicycles.json"));
    }

    [Fact]
    public async Task APaletteBelowTheContrast_IsNotPublished()
    {
        await SeedAsync();
        _context.SeedThemeFile(StoreA, "config/palette.json", """{ "color_neutral_950": "#777777", "color_neutral_50": "#ffffff" }""", draft: true);

        var result = await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, dryRun = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("'body-text' has a contrast of 4.47:1", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserOfStoreA_CannotPublishTheThemeOfStoreB()
    {
        await _context.SeedThemeAsync(StoreB);

        var result = await _context.CallAsync(StoreAPublisher(), Tool, new { store_id = StoreB, expected_versions = new Dictionary<string, string>() }, Approved);

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }
}
