using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.MCP;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class TemplateToolsTests : IAsyncDisposable
{
    private const string Rules = """{ "version": 2, "product": [ { "id": "bikes", "template": "product.bicycles", "when": { "inCategory": ["bikes"] } } ] }""";

    private readonly McpTestContext _context = new();

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Designer() => McpTestUser.Create("designer").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Templates);

    private static McpTestUser StoreADesigner() =>
        McpTestUser.Create("store-a-designer").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update, Permissions.Templates);

    private async Task SeedAsync()
    {
        _context.UsePageStore();
        await _context.SeedThemeAsync(StoreA);
        await _context.SeedThemeAsync(StoreB);
        _context.SeedThemeFile(StoreA, "templates/product.bicycles.json", ProductTemplate, draft: true);
        _context.SeedThemeFile(StoreA, "config/template_rules.json", Rules);
    }

    [Fact]
    public void TheEdit_NeedsBuilderUpdateAndTemplates()
    {
        var tool = _context.Harness.GetTool("edit_template");

        Assert.True(_context.Harness.IsVisible(tool, Designer()));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update)));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("theme").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Theme)));
    }

    [Fact]
    public async Task TheList_TellsEachTemplate_AndTheRulesThatGiveItPages()
    {
        await SeedAsync();

        var search = (await _context.CallAsync(Reader(), "search_templates", new { store_id = StoreA })).EnsureSucceeded().Read<TemplateSearch>();

        Assert.Equal(["product", "product.bicycles"], search.Items.Select(x => x.Key));
        var bicycles = search.Items[1];
        Assert.Equal((false, false, true, "product"), (bicycles.IsDefault, bicycles.Published, bicycles.HasDraft, bicycles.PageType));
        Assert.Equal(["bikes"], bicycles.Rules);
        Assert.Contains(search.Rules, x => x.Id == "bikes" && x.PageType == "product" && x.Position == 0 && !x.IsDefault);
        Assert.False(string.IsNullOrEmpty(search.RulesVersionToken));
    }

    [Fact]
    public async Task OneTemplate_IsReadWithItsSectionsAndVersion()
    {
        await SeedAsync();

        var search = (await _context.CallAsync(Reader(), "search_templates", new { store_id = StoreA, template_key = "product" })).EnsureSucceeded().Read<TemplateSearch>();

        Assert.Equal(["main", "reviews"], search.Template.Sections.Select(x => x["id"]!.GetValue<string>()));
        Assert.Equal(("product-info", "Shown"), (search.Template.MainSection, search.Template.MainSectionState));
        Assert.False(string.IsNullOrEmpty(search.Template.VersionToken));
    }

    [Fact]
    public async Task TheSectionFilter_FindsTheTemplatesThatHoldTheSection()
    {
        await SeedAsync();
        _context.SeedThemeFile(StoreA, "templates/category.json", """{ "settings": {}, "content": [ { "id": "list", "type": "category-listing" } ] }""");

        var search = (await _context.CallAsync(Reader(), "search_templates", new { store_id = StoreA, section_type = "product-reviews" })).EnsureSucceeded().Read<TemplateSearch>();

        Assert.Equal(["product", "product.bicycles"], search.Items.Select(x => x.Key));
        Assert.All(search.Items, x => Assert.Equal("reviews", Assert.Single(x.Sections).Id));
    }

    [Fact]
    public async Task AUserOfStoreA_CannotReadOrEditTheTemplatesOfStoreB()
    {
        await SeedAsync();

        var read = await _context.CallAsync(StoreAReader(), "search_templates", new { store_id = StoreB });
        var edit = await _context.CallAsync(StoreADesigner(), "edit_template", new { store_id = StoreB, template_key = "product.bikes", copy_of = "product" });

        Assert.Equal(McpToolCallOutcome.Forbidden, read.Outcome);
        Assert.Equal(McpToolCallOutcome.Forbidden, edit.Outcome);
        Assert.Null(_context.ReadThemeFile(StoreB, "templates/product.bikes.json", draft: true));
    }

    [Fact]
    public async Task ACopy_CreatesTheAlternateTemplateAsADraft()
    {
        await SeedAsync();

        var change = (await _context.CallAsync(Designer(), "edit_template", new
        {
            store_id = StoreA,
            template_key = "product.gloves",
            copy_of = "product",
            operations = new[] { new { op = "add_section", type = "text", settings = new { title = "Sizes" } } },
        })).EnsureSucceeded().Read<ThemeFileChange>();

        var saved = JsonNode.Parse(_context.ReadThemeFile(StoreA, "templates/product.gloves.json", draft: true)!)!;
        Assert.Equal(["product-info", "product-reviews", "text"], saved["content"]!.AsArray().Select(x => x!["type"]!.GetValue<string>()));
        Assert.Null(_context.ReadThemeFile(StoreA, "templates/product.gloves.json"));
        Assert.Equal("create_template", change.Action);
        Assert.Contains(change.Warnings, x => x.Contains("edit_template_rules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADryRun_WritesNothing_AndShowsTheSectionChanges()
    {
        await SeedAsync();
        var before = _context.Blobs.Files.ToDictionary(x => x.Key, x => x.Value);
        var token = await VersionOfAsync("product");

        var change = (await _context.CallAsync(Designer(), "edit_template", new
        {
            store_id = StoreA,
            template_key = "product",
            version_token = token,
            operations = new[] { new { op = "hide_section", section_id = "reviews" } },
            dryRun = true,
        })).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.True(change.DryRun);
        Assert.Contains(change.Changes, x => x.Field == "section product-reviews 'reviews' hidden" && x.To?.ToString() == "true");
        Assert.Equal(before, _context.Blobs.Files);
        Assert.Empty(_context.SavedVersions.Records);
    }

    [Fact]
    public async Task AnEdit_SavesTheDraft_AndAnswersTheNewVersion()
    {
        await SeedAsync();
        var token = await VersionOfAsync("product");

        var change = (await _context.CallAsync(Designer(), "edit_template", new
        {
            store_id = StoreA,
            template_key = "product",
            version_token = token,
            operations = new[] { new { op = "move_section", section_id = "reviews", position = 0 } },
        })).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Equal(["reviews", "main"], change.Sections.Select(x => x.Id));
        Assert.NotEqual(token, change.VersionTokens["default/templates/product.json"]);
        Assert.Contains("\"reviews\"", _context.ReadThemeFile(StoreA, "templates/product.json", draft: true), StringComparison.Ordinal);
        Assert.Single(_context.SavedVersions.Records);
    }

    [Theory]
    [InlineData("remove_section", "removed")]
    [InlineData("hide_section", "hidden")]
    public async Task TheMainSection_CannotBeRemovedOrHidden(string op, string verb)
    {
        await SeedAsync();

        var result = await _context.CallAsync(Designer(), "edit_template", new
        {
            store_id = StoreA,
            template_key = "product",
            version_token = await VersionOfAsync("product"),
            operations = new[] { new { op, section_id = "main" } },
        });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains($"its main section 'product-info' cannot be {verb}", result.Error, StringComparison.Ordinal);
        Assert.Contains("the product itself", result.Error, StringComparison.Ordinal);
        Assert.Null(_context.ReadThemeFile(StoreA, "templates/product.json", draft: true));
    }

    [Fact]
    public async Task AStaleVersion_IsRefused()
    {
        await SeedAsync();

        var result = await _context.CallAsync(Designer(), "edit_template", new
        {
            store_id = StoreA,
            template_key = "product",
            version_token = "old",
            operations = new[] { new { op = "hide_section", section_id = "reviews" } },
        });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("search_templates", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATemplate_CannotHoldASharedComponent()
    {
        await SeedAsync();

        var result = await _context.CallAsync(Designer(), "edit_template", new
        {
            store_id = StoreA,
            template_key = "product",
            version_token = await VersionOfAsync("product"),
            operations = new[] { new { op = "place_shared_component", shared_component_id = "c1" } },
        });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("pages only", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALayout_IsForPageTemplatesOnly()
    {
        await SeedAsync();

        var result = await _context.CallAsync(Designer(), "edit_template", new
        {
            store_id = StoreA,
            template_key = "product",
            version_token = await VersionOfAsync("product"),
            layout = "blank",
        });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("page templates only", result.Error, StringComparison.Ordinal);
    }

    private async Task<string> VersionOfAsync(string key) =>
        (await _context.CallAsync(Reader(), "search_templates", new { store_id = StoreA, template_key = key })).EnsureSucceeded().Read<TemplateSearch>().Template.VersionToken;
}
