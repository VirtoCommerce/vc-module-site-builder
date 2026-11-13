using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.MCP;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class ThemeDraftToolsTests : IAsyncDisposable
{
    private const string Tool = "edit_theme_draft";
    private const string Header = """{ "type": "header", "name": "Header", "content": [ { "id": "top", "type": "site-header" } ] }""";

    private readonly McpTestContext _context = new();

    public ThemeDraftToolsTests()
    {
        _context.UsePageStore();
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Designer() => McpTestUser.Create("designer").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Theme);

    private static McpTestUser StoreADesigner() =>
        McpTestUser.Create("store-a-designer").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update, Permissions.Theme);

    [Fact]
    public void TheTool_NeedsBuilderUpdateAndTheme()
    {
        var tool = _context.Harness.GetTool(Tool);

        Assert.True(_context.Harness.IsVisible(tool, Designer()));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update)));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("templates").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Templates)));
    }

    [Fact]
    public async Task TheHeader_IsReadAndEditedWithTheSectionOperations()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "sections/header-group.json", Header);

        var read = (await _context.CallAsync(Designer(), Tool, new { store_id = StoreA, part = "header", dryRun = true })).EnsureSucceeded().Read<ThemeFileChange>();
        var change = (await _context.CallAsync(Designer(), Tool, new
        {
            store_id = StoreA,
            part = "header",
            version_token = read.VersionTokens["default/sections/header-group.json"],
            operations = new[] { new { op = "add_section", type = "announcement-bar", settings = new { text = "Free shipping" }, position = 0 } },
        })).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Equal(["top"], read.Sections.Select(x => x.Id));
        Assert.Equal(["announcement-bar", "site-header"], change.Sections.Select(x => x.Type));
        var saved = JsonNode.Parse(_context.ReadThemeFile(StoreA, "sections/header-group.json", draft: true)!)!.AsObject();
        Assert.Equal(["type", "name", "content"], saved.Select(x => x.Key));
    }

    [Fact]
    public async Task TheBrand_StartsFromTheLogosOfTheSettings_WhenTheThemeHasNoBrandFile()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "config/settings_data.json", """{ "current": { "logo_image": "/logo.svg", "favicon_image": "/favicon.ico" } }""");

        var change = (await _context.CallAsync(Designer(), Tool, new { store_id = StoreA, part = "brand", favicon_url = "/new.ico" })).EnsureSucceeded().Read<ThemeFileChange>();

        var saved = JsonNode.Parse(_context.ReadThemeFile(StoreA, "config/brand.json", draft: true)!)!;
        Assert.Equal(("/logo.svg", "/new.ico"), (saved["logoUrl"]!.GetValue<string>(), saved["faviconUrl"]!.GetValue<string>()));
        Assert.Contains(change.Changes, x => x.Field == "faviconUrl" && x.To?.ToString() == "/new.ico");
    }

    [Fact]
    public async Task APaletteDryRun_ShowsTheFailingPairs_AndWritesNothing()
    {
        await _context.SeedThemeAsync(StoreA);
        var before = _context.Blobs.Files.ToDictionary(x => x.Key, x => x.Value);

        var change = (await _context.CallAsync(Designer(), Tool, new
        {
            store_id = StoreA,
            part = "palette",
            // #777777 on white is 4.47:1, short of the 4.5:1 body text needs
            light_colors = new Dictionary<string, string> { ["color_neutral_950"] = "#777777", ["color_neutral_50"] = "#ffffff" },
            dryRun = true,
        })).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.True(change.DryRun);
        var failure = Assert.Single(change.ContrastFailures, x => x.Pair == "body-text");
        Assert.Equal((4.47, "#777777"), (failure.Ratio, failure.TextColor));
        Assert.Contains(change.Warnings, x => x.Contains("'body-text' has a contrast of 4.47:1", StringComparison.Ordinal) && x.Contains("cannot be published", StringComparison.Ordinal));
        Assert.Equal(before, _context.Blobs.Files);
    }

    [Fact]
    public async Task APaletteGenerated_FromABrandColor_WritesTheLightAndDarkDrafts()
    {
        await _context.SeedThemeAsync(StoreA);

        var change = (await _context.CallAsync(Designer(), Tool, new { store_id = StoreA, part = "palette", primary_color = "#0A5F38" })).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.False(change.DryRun);
        Assert.Empty(change.ContrastFailures);
        Assert.Equal(StorefrontPaletteTokens.Light.Order(), JsonNode.Parse(_context.ReadThemeFile(StoreA, "config/palette.json", draft: true)!)!.AsObject().Select(x => x.Key).Order());
        Assert.NotNull(_context.ReadThemeFile(StoreA, "config/palette.dark.json", draft: true));
    }

    [Fact]
    public async Task AUserOfStoreA_CannotEditTheThemeOfStoreB()
    {
        await _context.SeedThemeAsync(StoreB);

        var result = await _context.CallAsync(StoreADesigner(), Tool, new { store_id = StoreB, part = "brand", logo_url = "/logo.svg" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Null(_context.ReadThemeFile(StoreB, "config/brand.json", draft: true));
    }

    [Fact]
    public async Task AnArgumentOfAnotherPart_IsRefused()
    {
        await _context.SeedThemeAsync(StoreA);

        var result = await _context.CallAsync(Designer(), Tool, new { store_id = StoreA, part = "brand", primary_color = "#0a5f38" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("do not apply to part 'brand'", result.Error, StringComparison.Ordinal);
    }
}
