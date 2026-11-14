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

public sealed class TemplateRuleToolsTests : IAsyncDisposable
{
    private const string Tool = "edit_template_rules";
    private const string Rules = """
        { "version": 2, "conditions": ["inCategory"], "product": [
            { "id": "bikes", "template": "product.bicycles", "when": { "inCategory": ["bikes"] } },
            { "id": "gloves", "template": "product.bicycles", "when": { "inCategory": ["gloves"] } }
        ], "custom": { "kept": true } }
        """;

    private readonly McpTestContext _context = new();

    public TemplateRuleToolsTests()
    {
        _context.UsePageStore();
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Designer() => McpTestUser.Create("designer").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Templates);

    private async Task SeedAsync()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "templates/product.bicycles.json", ProductTemplate);
        _context.SeedThemeFile(StoreA, "config/template_rules.json", Rules);
    }

    private async Task<string> VersionAsync() =>
        (await _context.CallAsync(Reader(), "search_templates", new { store_id = StoreA })).EnsureSucceeded().Read<TemplateSearch>().RulesVersionToken;

    [Fact]
    public void TheTool_NeedsBuilderUpdateAndTemplates()
    {
        var tool = _context.Harness.GetTool(Tool);

        Assert.True(_context.Harness.IsVisible(tool, Designer()));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update)));
    }

    [Fact]
    public async Task ADryRun_ShowsTheRulesBeforeAndAfter_AndWritesNothing()
    {
        await SeedAsync();

        var change = (await _context.CallAsync(Designer(), Tool, new
        {
            store_id = StoreA,
            version_token = await VersionAsync(),
            operations = new object[]
            {
                new { op = "move", rule_id = "gloves", position = 0 },
                new { op = "disable", rule_id = "bikes" },
            },
            dryRun = true,
        })).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Contains(change.Changes, x => x.Field == "order of product rules" && x.From?.ToString() == "bikes, gloves" && x.To?.ToString() == "gloves, bikes");
        Assert.Contains(change.Changes, x => x.Field == "product rule 'bikes'" && x.To!.ToString()!.EndsWith("(disabled)", StringComparison.Ordinal));
        Assert.Equal(["gloves", "bikes"], change.Rules.Where(x => x.PageType == "product").Select(x => x.Id));
        Assert.Null(_context.ReadThemeFile(StoreA, "config/template_rules.json", draft: true));
    }

    [Fact]
    public async Task AnAddedRule_IsSavedInTheDesignersFormat_KeepingWhatTheFileHeld()
    {
        await SeedAsync();

        (await _context.CallAsync(Designer(), Tool, new
        {
            store_id = StoreA,
            version_token = await VersionAsync(),
            operations = new object[] { new { op = "add", template = "product.bicycles", when = new { productType = "Bike", inCategory = "a, b" }, position = 0 } },
        })).EnsureSucceeded();

        var saved = JsonNode.Parse(_context.ReadThemeFile(StoreA, "config/template_rules.json", draft: true)!)!;
        var added = saved["product"]![0]!;
        Assert.Equal(["a", "b"], added["when"]!["inCategory"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["inCategory", "productType"], saved["conditions"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(saved["custom"]!["kept"]!.GetValue<bool>());
        Assert.Equal(2, saved["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task ARule_MayPickOnlyATemplateTheStorefrontHas()
    {
        await SeedAsync();

        var result = await _context.CallAsync(Designer(), Tool, new
        {
            store_id = StoreA,
            version_token = await VersionAsync(),
            operations = new object[] { new { op = "add", template = "product.missing" } },
        });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("no template 'product.missing'", result.Error, StringComparison.Ordinal);
        Assert.Null(_context.ReadThemeFile(StoreA, "config/template_rules.json", draft: true));
    }

    [Theory]
    [InlineData("path", "/bikes/*", "does not apply to product rules")]
    [InlineData("weather", "sunny", "is not a condition of the storefront")]
    public async Task AConditionOutsideTheVocabulary_IsRefused(string name, string value, string error)
    {
        await SeedAsync();

        var result = await _context.CallAsync(Designer(), Tool, new
        {
            store_id = StoreA,
            version_token = await VersionAsync(),
            operations = new object[] { new { op = "change", rule_id = "bikes", when = new JsonObject { [name] = value } } },
        });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains(error, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemovedRule_IsGone_AndARuleAfterARuleWithoutConditionsIsWarnedAbout()
    {
        await SeedAsync();

        var change = (await _context.CallAsync(Designer(), Tool, new
        {
            store_id = StoreA,
            version_token = await VersionAsync(),
            operations = new object[]
            {
                new { op = "remove", rule_id = "gloves" },
                new { op = "add", template = "product.bicycles", position = 0 },
            },
        })).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Contains(change.Changes, x => x.Field == "product rule 'gloves'" && x.To?.ToString() == "removed");
        Assert.Contains(change.Warnings, x => x.Contains("never pick a template", StringComparison.Ordinal));
        Assert.DoesNotContain("gloves", _context.ReadThemeFile(StoreA, "config/template_rules.json", draft: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleVersion_IsRefused()
    {
        await SeedAsync();

        var result = await _context.CallAsync(Designer(), Tool, new
        {
            store_id = StoreA,
            version_token = "old",
            operations = new object[] { new { op = "remove", rule_id = "gloves" } },
        });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("rules_version_token", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutARulesFile_TheDefaultProductRuleIsListed_AndBecomesTheStoresOwnWhenChanged()
    {
        await _context.SeedThemeAsync(StoreA);
        var search = (await _context.CallAsync(Reader(), "search_templates", new { store_id = StoreA })).EnsureSucceeded().Read<TemplateSearch>();

        var change = (await _context.CallAsync(Designer(), Tool, new
        {
            store_id = StoreA,
            version_token = search.RulesVersionToken,
            operations = new object[] { new { op = "disable", rule_id = "default-b2c" } },
        })).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.True(Assert.Single(search.Rules).IsDefault);
        Assert.False(change.Rules.Single().IsDefault);
        Assert.Contains("default-b2c", _context.ReadThemeFile(StoreA, "config/template_rules.json", draft: true), StringComparison.Ordinal);
    }
}
