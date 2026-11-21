using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.MCP.Services;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class SaveSectionTypeToolTests : IAsyncDisposable
{
    private const string Tool = "save_section_type";
    private const string Type = "custom-stats";
    private const string File = "custom-sections/custom-stats.json";
    private const string Path = "default/custom-sections/custom-stats.json";
    private const string Template = "<section class=\"stats\">\n  <h2>{{ settings.title }}</h2>\n  <span data-count-up=\"{{ settings.value }}\">0</span>\n</section>";
    private const string Css = ":scope { padding: 2rem; }\n.stats h2 { color: var(--color-neutral-900); }";

    private static readonly object[] Settings = [new { id = "title", type = "string", translatable = true }, new { id = "value", type = "number" }];

    private readonly McpTestContext _context = new();

    public SaveSectionTypeToolTests()
    {
        _context.UsePageStore();
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Designer() => McpTestUser.Create("designer").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Theme);

    private Task SeedAsync() => _context.SeedThemeAsync(StoreA, StorefrontKit);

    private Task<McpToolCallResult> SaveAsync(McpTestUser user = null, string template = Template, string css = Css, object[] settings = null,
        string type = Type, string storeId = StoreA, string versionToken = null, bool dryRun = false) =>
        _context.CallAsync(user ?? Designer(), Tool, new
        {
            store_id = storeId,
            type,
            name = new Dictionary<string, string> { ["en-US"] = "Stats" },
            schema = new { settings = settings ?? Settings },
            template,
            css,
            version_token = versionToken,
            dryRun,
        });

    [Fact]
    public void TheTool_NeedsBuilderUpdateAndTheme()
    {
        var tool = _context.Harness.GetTool(Tool);

        Assert.True(_context.Harness.IsVisible(tool, Designer()));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("templates").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Templates)));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("no-update").WithPermissions(Permissions.Read, Permissions.Theme)));
    }

    [Fact]
    public async Task ADryRun_AnswersTheErrorsThatRefuseTheType_AndSavesNothing()
    {
        await SeedAsync();

        var change = (await SaveAsync(template: "<section onclick=\"go()\"><h2>{{ settings.subtitle }}</h2></section>", dryRun: true))
            .EnsureSucceeded().Read<ThemeFileChange>();

        Assert.True(change.DryRun);
        Assert.Equal(
            [
                $"{File}: <section> at line 1 has an attribute a custom section cannot use, 'onclick': it runs script",
                $"{File}: <h2> at line 1 binds 'settings.subtitle', a setting the schema does not declare",
            ],
            change.Errors);
        Assert.DoesNotContain(change.Warnings, x => x.Contains("publish_theme", StringComparison.Ordinal));
        Assert.Null(_context.ReadThemeFile(StoreA, File, draft: true));
    }

    [Fact]
    public async Task ADryRun_AnswersTheWarningsAndTheChanges_AndSavesNothing()
    {
        await SeedAsync();

        var change = (await SaveAsync(template: "<section><h2>Our numbers</h2><p>{{ settings.title }}</p></section>", css: ":scope { color: #000; }", dryRun: true))
            .EnsureSucceeded().Read<ThemeFileChange>();

        Assert.True(change.DryRun);
        Assert.Null(change.Errors);
        Assert.Contains($"{File}: the template holds text of its own ('Our numbers'), which is the same in every language: bind it to a translatable setting", change.Warnings);
        Assert.Contains(change.Warnings, x => x.StartsWith($"{File}: the CSS sets literal colors (color)", StringComparison.Ordinal));
        Assert.Contains(change.Changes, x => x.Field == "template line 1" && x.From == null && x.To?.ToString() == "<section><h2>Our numbers</h2><p>{{ settings.title }}</p></section>");
        Assert.Contains(change.Changes, x => x.Field == "schema.settings.title" && x.From == null);
        Assert.Null(_context.ReadThemeFile(StoreA, File, draft: true));
    }

    [Fact]
    public async Task TheSave_WritesTheTypeAsADraftOfTheTheme()
    {
        await SeedAsync();

        var change = (await SaveAsync()).EnsureSucceeded().Read<ThemeFileChange>();

        var saved = JsonNode.Parse(_context.ReadThemeFile(StoreA, File, draft: true)!)!;
        Assert.Equal((1, "Stats", Template, Css), (saved["format"]!.GetValue<int>(), saved["name"]!["en-US"]!.GetValue<string>(),
            saved["template"]!.GetValue<string>(), saved["css"]!.GetValue<string>()));
        Assert.Null(_context.ReadThemeFile(StoreA, File));
        Assert.False(change.DryRun);
        Assert.Equal("create_section_type", change.Action);
        Assert.False(string.IsNullOrEmpty(change.VersionTokens[Path]));
        Assert.Contains(change.Warnings, x => x.Contains("edit_page_draft", StringComparison.Ordinal));
        Assert.Contains(change.Warnings, x => x.Contains("publish_theme", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondSave_ReplacesTheType_AndListsWhatChanged()
    {
        await SeedAsync();
        await SaveAsync();

        var change = (await SaveAsync(
                css: ":scope { padding: 4rem; }\n.stats h2 { color: var(--color-neutral-900); }",
                settings: [.. Settings, new { id = "note", type = "string", translatable = true }]))
            .EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Equal("save_section_type", change.Action);
        Assert.Equal(["schema.settings.note", "css line 1"], change.Changes.Select(x => x.Field));
        Assert.Equal((":scope { padding: 2rem; }", ":scope { padding: 4rem; }"), (change.Changes[1].From?.ToString(), change.Changes[1].To?.ToString()));
        Assert.Contains("padding: 4rem", JsonNode.Parse(_context.ReadThemeFile(StoreA, File, draft: true)!)!["css"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionTokenOfAnOlderVersion_IsRefused()
    {
        await SeedAsync();
        var first = (await SaveAsync()).EnsureSucceeded().Read<ThemeFileChange>();
        await SaveAsync(css: ":scope { padding: 3rem; }");

        var result = await SaveAsync(css: ":scope { padding: 4rem; }", versionToken: first.VersionTokens[Path]);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since you read it", result.Error, StringComparison.Ordinal);
        Assert.Contains("padding: 3rem", _context.ReadThemeFile(StoreA, File, draft: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrokenType_IsRefusedWhenSaved_AndNothingIsWritten()
    {
        await SeedAsync();

        var result = await SaveAsync(template: "<section onclick=\"go()\"></section>");

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("'onclick': it runs script", result.Error, StringComparison.Ordinal);
        Assert.Null(_context.ReadThemeFile(StoreA, File, draft: true));
    }

    [Fact]
    public async Task ABehaviorTheStorefrontDoesNotOffer_IsAnError()
    {
        await SeedAsync();

        var change = (await SaveAsync(template: "<section data-marquee><h2>{{ settings.title }}</h2></section>", dryRun: true)).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Contains(change.Errors, x => x.Contains("'data-marquee'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("stats")]
    [InlineData("custom-Stats")]
    [InlineData("hero-banner")]
    public async Task ATypeThatIsNotCustom_IsRefused(string type)
    {
        await SeedAsync();

        var result = await SaveAsync(type: type);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("type must be 'custom-'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserOfStoreA_CannotSaveATypeOfStoreB()
    {
        await SeedAsync();
        await _context.SeedThemeAsync(StoreB, StorefrontKit);
        var user = McpTestUser.Create("store-a-designer").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update, Permissions.Theme);

        var result = await SaveAsync(user, storeId: StoreB);

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Null(_context.ReadThemeFile(StoreB, File, draft: true));
    }

    [Fact]
    public async Task ASavedType_IsPlacedOnAPage_WithItsOwnSettings()
    {
        await SeedAsync();
        await SaveAsync();
        _context.SeedDraft("landing", StoreA, """{ "settings": { "name": "Landing" }, "content": [] }""");
        var editor = McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update);

        var edit = await _context.CallAsync(editor, "edit_page_draft", new
        {
            page_id = "landing",
            version_token = await _context.DraftVersionAsync("landing"),
            operations = new[] { new { op = "add_section", type = Type, settings = new { title = "Our year", value = 42 } } },
        });

        edit.EnsureSucceeded();
        var section = JsonNode.Parse(await _context.DraftContentAsync("landing"))!["content"]![0]!;
        Assert.Equal((Type, "Our year"), (section["type"]!.GetValue<string>(), section["title"]!.GetValue<string>()));
    }
}
