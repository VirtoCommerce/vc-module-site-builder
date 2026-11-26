using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP.Services;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class SectionTypeToolsTests : IAsyncDisposable
{
    private const string Create = "create_section_type";
    private const string Change = "change_section_type";
    private const string Type = "custom-stats";
    private const string File = "custom-sections/custom-stats.json";
    private const string Path = "default/custom-sections/custom-stats.json";
    private const string Template = "<section class=\"custom-stats\">\n  <h2>{{ settings.title }}</h2>\n  <span data-count-up=\"{{ settings.value }}\">0</span>\n</section>";
    private const string Css = ":scope { padding: 2rem; }\n.custom-stats h2 { color: var(--color-neutral-900); }";

    private static readonly object[] Settings = [new { id = "title", type = "string", translatable = true }, new { id = "value", type = "number" }];

    private readonly McpTestContext _context = new();
    private readonly List<GroupedSiteBuilderPage> _pages = [];

    public SectionTypeToolsTests()
    {
        _context.UsePageStore();
        _context.ListPages(() => _pages.Select(LandingRow));
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Designer() => McpTestUser.Create("designer").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Theme);

    private Task<string> SeedAsync() => _context.SeedThemeAsync(StoreA, StorefrontKit);

    private Task<McpToolCallResult> CallAsync(string tool, McpTestUser user = null, string template = Template, string css = Css, object[] settings = null,
        string type = Type, string storeId = StoreA, string versionToken = null, bool dryRun = false)
    {
        var arguments = new Dictionary<string, object>
        {
            ["store_id"] = storeId,
            ["type"] = type,
            ["name"] = new Dictionary<string, string> { ["en-US"] = "Stats" },
            ["schema"] = new { settings = settings ?? Settings },
            ["template"] = template,
            ["css"] = css,
            ["dryRun"] = dryRun,
        };
        if (versionToken != null)
        {
            arguments["version_token"] = versionToken;
        }

        return _context.CallAsync(user ?? Designer(), tool, arguments);
    }

    [Theory]
    [InlineData(Create)]
    [InlineData(Change)]
    public void TheTools_NeedBuilderUpdateAndTheme(string name)
    {
        var tool = _context.Harness.GetTool(name);

        Assert.True(_context.Harness.IsVisible(tool, Designer()));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("templates").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Templates)));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("no-update").WithPermissions(Permissions.Read, Permissions.Theme)));
    }

    [Fact]
    public async Task ADryRun_AnswersTheErrorsThatRefuseTheType_AndSavesNothing()
    {
        await SeedAsync();

        var change = (await CallAsync(Create, template: "<section onclick=\"go()\"><h2>{{ settings.subtitle }}</h2></section>", dryRun: true))
            .EnsureSucceeded().Read<ThemeFileChange>();

        Assert.True(change.DryRun);
        Assert.Equal(
            [
                $"{File}: <section> at line 1 has an attribute a custom section cannot use, 'onclick': it runs script",
                $"{File}: <h2> at line 1 binds 'settings.subtitle', a setting the schema does not declare",
            ],
            change.Errors);
        Assert.Null(change.NextStep);
        Assert.Null(_context.ReadThemeFile(StoreA, File, draft: true));
    }

    [Fact]
    public async Task ADryRun_AnswersTheWarningsAndTheChanges_AndSavesNothing()
    {
        await SeedAsync();

        var change = (await CallAsync(Create, template: "<section><h2>Our numbers</h2><p>{{ settings.title }}</p></section>", css: ":scope { color: #000; }", dryRun: true))
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
    public async Task TheCreation_WritesTheTypeAsADraftOfTheTheme()
    {
        await SeedAsync();

        var change = (await CallAsync(Create)).EnsureSucceeded().Read<ThemeFileChange>();

        var saved = JsonNode.Parse(_context.ReadThemeFile(StoreA, File, draft: true)!)!;
        Assert.Equal((1, "Stats", Template, Css), (saved["format"]!.GetValue<int>(), saved["name"]!["en-US"]!.GetValue<string>(),
            saved["template"]!.GetValue<string>(), saved["css"]!.GetValue<string>()));
        Assert.Null(_context.ReadThemeFile(StoreA, File));
        Assert.False(change.DryRun);
        Assert.Equal(Create, change.Action);
        Assert.False(string.IsNullOrEmpty(change.VersionTokens[Path]));
        Assert.Empty(change.Warnings);
        Assert.Contains("edit_page_draft", change.NextStep, StringComparison.Ordinal);
        Assert.Contains("publish_theme", change.NextStep, StringComparison.Ordinal);
        Assert.Contains($"version_tokens['{Path}']", change.NextStep, StringComparison.Ordinal);
        Assert.DoesNotContain("dryRun", change.NextStep, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Create)]
    [InlineData(Change)]
    public async Task ADryRunOfACleanType_AnswersNoWarnings_AndTheNextStepApart(string tool)
    {
        await SeedAsync();
        string versionToken = null;
        if (tool == Change)
        {
            versionToken = (await CallAsync(Create, css: ":scope { padding: 1rem; }")).EnsureSucceeded().Read<ThemeFileChange>().VersionTokens[Path];
        }

        var change = (await CallAsync(tool, versionToken: versionToken, dryRun: true)).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Empty(change.Warnings);
        Assert.StartsWith($"Call {tool} again without dryRun to save the type.", change.NextStep, StringComparison.Ordinal);
        Assert.Contains("publish_theme", change.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACreation_OfATypeTheThemeHas_IsRefused_AndTheTypeStaysAsItIs()
    {
        await SeedAsync();
        await CallAsync(Create);

        var result = await CallAsync(Create, css: ":scope { padding: 4rem; }");

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("change_section_type", result.Error, StringComparison.Ordinal);
        Assert.Contains("padding: 2rem", _context.ReadThemeFile(StoreA, File, draft: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChange_ReplacesTheType_AndListsWhatChanged()
    {
        await SeedAsync();
        var created = (await CallAsync(Create)).EnsureSucceeded().Read<ThemeFileChange>();

        var change = (await CallAsync(Change,
                css: ":scope { padding: 4rem; }\n.custom-stats h2 { color: var(--color-neutral-900); }",
                settings: [.. Settings, new { id = "note", type = "string", translatable = true }], versionToken: created.VersionTokens[Path]))
            .EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Equal(Change, change.Action);
        Assert.Equal(["schema.settings.note", "css line 1"], change.Changes.Select(x => x.Field));
        Assert.Equal((":scope { padding: 2rem; }", ":scope { padding: 4rem; }"), (change.Changes[1].From?.ToString(), change.Changes[1].To?.ToString()));
        Assert.Contains("padding: 4rem", JsonNode.Parse(_context.ReadThemeFile(StoreA, File, draft: true)!)!["css"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChange_OfMalformedSettingsToAList_IsAChange_AndIsSaved()
    {
        const string template = "<section class=\"custom-stats\"><h2>Stats</h2></section>";
        await SeedAsync();
        var current = new JsonObject
        {
            ["format"] = 1,
            ["name"] = new JsonObject { ["en-US"] = "Stats" },
            ["schema"] = new JsonObject { ["settings"] = "invalid" },
            ["template"] = template,
            ["css"] = Css,
        }.ToJsonString();
        _context.SeedThemeFile(StoreA, File, current, draft: true);
        var token = SiteBuilderContentVersion.CreateForThemeFile(StoreA, Path, current);

        var change = (await CallAsync(Change, template: template, settings: [], versionToken: token)).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Contains(change.Changes, x => x.Field == "schema.settings" && x.From?.ToString() == "invalid");
        Assert.Equal("[]", JsonNode.Parse(_context.ReadThemeFile(StoreA, File, draft: true)!)!["schema"]!["settings"]!.ToJsonString());
    }

    [Fact]
    public async Task AChange_OfASettingWhoseTypeIsNotAText_IsSaved_AndListsWhatChanged()
    {
        await SeedAsync();
        var current = new JsonObject
        {
            ["format"] = 1,
            ["name"] = new JsonObject { ["en-US"] = "Stats" },
            ["schema"] = new JsonObject { ["settings"] = new JsonArray(new JsonObject { ["id"] = "title", ["type"] = 5 }) },
            ["template"] = Template,
            ["css"] = Css,
        }.ToJsonString();
        _context.SeedThemeFile(StoreA, File, current, draft: true);
        var token = SiteBuilderContentVersion.CreateForThemeFile(StoreA, Path, current);

        var change = (await CallAsync(Change, versionToken: token)).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Equal(["value"], change.PlacedSections!.NewSettings.Select(x => x.Id));
        Assert.Equal("string", JsonNode.Parse(_context.ReadThemeFile(StoreA, File, draft: true)!)!["schema"]!["settings"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADryRunThatChangesNothing_StillAnswersTheErrorsOfTheType()
    {
        await SeedAsync();
        var created = (await CallAsync(Create)).EnsureSucceeded().Read<ThemeFileChange>();
        var broken = JsonNode.Parse(_context.ReadThemeFile(StoreA, File, draft: true)!)!;
        broken["template"] = "<section onclick=\"go()\"></section>";
        _context.SeedThemeFile(StoreA, File, broken.ToJsonString(), draft: true);
        var token = SiteBuilderContentVersion.CreateForThemeFile(StoreA, Path, broken.ToJsonString());

        var change = (await CallAsync(Change, template: "<section onclick=\"go()\"></section>", versionToken: token, dryRun: true)).EnsureSucceeded()
            .Read<ThemeFileChange>();

        Assert.NotEqual(created.VersionTokens[Path], token);
        Assert.Contains("The call changes nothing, so nothing was saved.", change.Warnings);
        Assert.Contains(change.Errors ?? [], x => x.Contains("'onclick': it runs script", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADryRunThatChangesNothing_AnswersNoNextStep()
    {
        await SeedAsync();
        var created = (await CallAsync(Create)).EnsureSucceeded().Read<ThemeFileChange>();

        var change = (await CallAsync(Change, versionToken: created.VersionTokens[Path], dryRun: true)).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Contains("The call changes nothing, so nothing was saved.", change.Warnings);
        Assert.Null(change.Errors);
        Assert.Null(change.NextStep);
    }

    [Fact]
    public async Task AChange_OfATypeTheThemeDoesNotHave_IsRefused()
    {
        await SeedAsync();

        var result = await CallAsync(Change);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("create_section_type", result.Error, StringComparison.Ordinal);
        Assert.Null(_context.ReadThemeFile(StoreA, File, draft: true));
    }

    [Fact]
    public async Task AChange_WithoutAVersionToken_IsRefused_AndTheTypeStaysAsItIs()
    {
        await SeedAsync();
        await CallAsync(Create);

        var result = await CallAsync(Change, css: ":scope { padding: 4rem; }");

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("Pass version_token", result.Error, StringComparison.Ordinal);
        Assert.Contains($"site-builder://stores/{StoreA}/custom-sections/{Type}", result.Error, StringComparison.Ordinal);
        Assert.Contains($"version_tokens['{Path}']", result.Error, StringComparison.Ordinal);
        Assert.Contains("padding: 2rem", _context.ReadThemeFile(StoreA, File, draft: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionTokenOfAnOlderVersion_IsRefused()
    {
        await SeedAsync();
        var created = (await CallAsync(Create)).EnsureSucceeded().Read<ThemeFileChange>();
        (await CallAsync(Change, css: ":scope { padding: 3rem; }", versionToken: created.VersionTokens[Path])).EnsureSucceeded();

        var result = await CallAsync(Change, css: ":scope { padding: 4rem; }", versionToken: created.VersionTokens[Path]);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since you read it", result.Error, StringComparison.Ordinal);
        Assert.Contains("padding: 3rem", _context.ReadThemeFile(StoreA, File, draft: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrokenType_IsRefusedWhenCreated_AndNothingIsWritten()
    {
        await SeedAsync();

        var result = await CallAsync(Create, template: "<section onclick=\"go()\"></section>");

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("'onclick': it runs script", result.Error, StringComparison.Ordinal);
        Assert.Null(_context.ReadThemeFile(StoreA, File, draft: true));
    }

    [Fact]
    public async Task ABehaviorTheStorefrontDoesNotOffer_IsAnError()
    {
        await SeedAsync();

        var change = (await CallAsync(Create, template: "<section data-marquee><h2>{{ settings.title }}</h2></section>", dryRun: true)).EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Contains(change.Errors, x => x.Contains("'data-marquee'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("stats")]
    [InlineData("custom-Stats")]
    [InlineData("hero-banner")]
    public async Task ATypeThatIsNotCustom_IsRefused(string type)
    {
        await SeedAsync();

        var result = await CallAsync(Create, type: type);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("is not a custom section type, which matches ^custom-", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserOfStoreA_CannotCreateATypeInStoreB()
    {
        await SeedAsync();
        await _context.SeedThemeAsync(StoreB, StorefrontKit);
        var user = McpTestUser.Create("store-a-designer").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update, Permissions.Theme);

        var result = await CallAsync(Create, user, storeId: StoreB);

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Null(_context.ReadThemeFile(StoreB, File, draft: true));
    }

    [Fact]
    public async Task ACreatedType_IsPlacedOnAPage_WithItsOwnSettings()
    {
        await SeedAsync();
        await CallAsync(Create);
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

    [Fact]
    public async Task AChange_ThatAddsAndRemovesSettings_ListsThePagesThatPlaceTheType_AndWhatTheirSectionsShow()
    {
        await SeedAsync();
        var created = (await CallAsync(Create)).EnsureSucceeded().Read<ThemeFileChange>();
        _pages.Add(_context.SeedDraft("landing", StoreA, """{ "settings": { "name": "Landing" }, "content": [ { "id": "s1", "type": "custom-stats", "title": "A" }, { "id": "s2", "type": "custom-stats", "title": "B" } ] }"""));
        _pages.Add(_context.SeedDraft("about", StoreA, """{ "settings": { "name": "About" }, "content": [ { "id": "t1", "type": "text" } ] }"""));

        var change = (await CallAsync(Change, versionToken: created.VersionTokens[Path], template: "<section class=\"custom-stats\">\n  <h2>{{ settings.title }}</h2>\n</section>", settings:
            [
                new { id = "title", type = "string", translatable = true },
                new { id = "note", type = "string", @default = "Order by 6 pm" },
                new { id = "items", type = "list", element = new[] { new { id = "text", type = "string" } }, @default = new[] { new { text = "a" }, new { text = "b" } } },
                new { id = "heading", type = "header" },
                new { id = "badge", type = "string" },
            ]))
            .EnsureSucceeded().Read<ThemeFileChange>();

        var placed = change.PlacedSections!;
        Assert.Equal(
            [("note", "its default 'Order by 6 pm'"), ("items", "its default, 2 rows"), ("badge", "nothing: the setting has no default")],
            placed.NewSettings.Select(x => (x.Id, x.PlacedSectionsShow)));
        Assert.Equal(["value"], placed.RemovedSettings);
        var page = Assert.Single(placed.Pages);
        Assert.Equal(("landing", 2), (page.PageId, page.Sections));
        Assert.Null(page.Version);
        Assert.Contains("Templates are not scanned", placed.Coverage, StringComparison.Ordinal);
        Assert.Contains("no longer shown", placed.Note, StringComparison.Ordinal);
        Assert.Contains("update_sections_bulk", change.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChange_ListsTheLanguageVersionsThatPlaceTheType_ByTheirOwnIds_AndTheSharedComponentsThatHoldIt_Apart()
    {
        await SeedAsync();
        var created = (await CallAsync(Create)).EnsureSucceeded().Read<ThemeFileChange>();
        var english = _context.SeedDraft("about-en", StoreA, """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""", multilanguagePageId: "about");
        var french = _context.SeedDraft("about-fr", StoreA,
            """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" }, { "id": "pl1", "type": "componentRef", "componentRef": "sc1" } ] }""",
            "fr-FR", "about");
        _context.ListPages(() =>
        [
            new SiteBuilderPageListItem
            {
                Type = "Multilanguage",
                Id = "about",
                Name = "About",
                Permalink = "/about",
                MultilanguagePage = Root("about", StoreA),
                Languages = [new MultilanguagePageLanguage { CultureName = "en-US", Page = english }, new MultilanguagePageLanguage { CultureName = "fr-FR", Page = french }],
            },
        ]);
        _context.SharedComponents
            .Setup(x => x.GetAsync(It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync([new SiteBuilderSharedComponent { Id = "sc1", StoreId = StoreA }]);
        _context.SharedComponentContents
            .Setup(x => x.TryLoadContentAsync(It.IsAny<SiteBuilderSharedComponent>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync("""{ "settings": {}, "content": [ { "id": "c1", "type": "custom-stats" } ] }""");
        var user = McpTestUser.Create("designer").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Theme, Permissions.SharedComponentsRead);

        var change = (await CallAsync(Change, user, settings: [.. Settings, new { id = "note", type = "string" }], versionToken: created.VersionTokens[Path], dryRun: true))
            .EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Equal([("about-en", "en-US", 1, ""), ("about-fr", "fr-FR", 1, "sc1")],
            change.PlacedSections!.Pages.Select(x => (x.PageId, x.Language, x.Sections, string.Join(",", x.SharedComponentIds))));
        Assert.Contains("change_shared_component", change.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChange_ThatKeepsTheSettings_ListsNoPlacements()
    {
        await SeedAsync();
        var created = (await CallAsync(Create)).EnsureSucceeded().Read<ThemeFileChange>();
        _pages.Add(_context.SeedDraft("landing", StoreA, """{ "settings": { "name": "Landing" }, "content": [ { "id": "s1", "type": "custom-stats", "title": "A" } ] }"""));

        var change = (await CallAsync(Change, css: ":scope { padding: 4rem; }", versionToken: created.VersionTokens[Path], dryRun: true))
            .EnsureSucceeded().Read<ThemeFileChange>();

        Assert.Null(change.PlacedSections);
        Assert.DoesNotContain("update_sections_bulk", change.NextStep, StringComparison.Ordinal);
        Assert.DoesNotContain("version_tokens", change.NextStep, StringComparison.Ordinal);
    }
}
