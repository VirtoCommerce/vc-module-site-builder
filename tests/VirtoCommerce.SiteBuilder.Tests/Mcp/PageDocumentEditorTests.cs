using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.MCP;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class PageDocumentEditorTests
{
    private const string Document = """
        {
          "settings": { "name": "Home", "seoInfo": { "pageTitle": "Old title" } },
          "content": [
            { "id": "hero", "type": "hero-banner", "title": "Spring", "button": { "url": "/sale", "text": "Shop" } },
            { "id": "list", "type": "features", "blocks": [ { "id": "b1", "type": "item", "label": "One" }, { "id": "b2", "type": "item", "label": "Two" } ] },
            { "id": "promo", "type": "componentRef", "componentRef": "sc1" },
            { "id": "note", "type": "notice", "text": { "en-US": "Hello", "fr-FR": "Bonjour" } }
          ]
        }
        """;

    private readonly FakeSources _sources = new();

    [Fact]
    public async Task AddSection_GoesToTheEnd_WithANewIdAndItsSettings()
    {
        var editor = await EditAsync(Op("add_section", type: "rich-text", settings: new() { ["title"] = "Hi", ["link.url"] = "/about" }));

        var added = Section(editor, 4);
        Assert.Equal("rich-text", Text(added["type"]));
        Assert.StartsWith("richtext", Text(added["id"]));
        Assert.Equal("Hi", Text(added["title"]));
        Assert.Equal("/about", Text(added["link"]!["url"]));
    }

    [Theory]
    [InlineData(0, null, null, 0)]
    [InlineData(null, "hero", null, 1)]
    [InlineData(null, null, "promo", 2)]
    public async Task AddSection_TakesThePlaceNamed(int? position, string after, string before, int expected)
    {
        var editor = await EditAsync(new PageEditOperation { Op = "add_section", Type = "text", Position = position, After = after, Before = before });

        Assert.Equal("text", Text(Section(editor, expected)["type"]));
    }

    [Fact]
    public async Task AddSection_WithTwoPlaces_IsRefused()
    {
        var error = await RefusedAsync(new PageEditOperation { Op = "add_section", Type = "text", Position = 0, After = "hero" });

        Assert.Contains("only one of position, after and before", error);
    }

    [Fact]
    public async Task AddSection_WithoutAType_IsRefused()
    {
        var error = await RefusedAsync(Op("add_section"));

        Assert.StartsWith("Operation 1 (add_section): Pass the type", error);
    }

    [Fact]
    public async Task DuplicateSection_PutsACopyWithNewIdsRightAfterIt()
    {
        var editor = await EditAsync(Op("duplicate_section", sectionId: "list"));

        var copy = Section(editor, 2);
        Assert.Equal("features", Text(copy["type"]));
        Assert.NotEqual("list", Text(copy["id"]));
        Assert.Equal(["One", "Two"], copy["blocks"]!.AsArray().Select(x => Text(x!["label"])));
        Assert.DoesNotContain(copy["blocks"]!.AsArray(), x => Text(x!["id"]) is "b1" or "b2");
    }

    [Fact]
    public async Task DuplicateSection_OfAPlacement_PlacesTheSameComponentAgain()
    {
        var editor = await EditAsync(Op("duplicate_section", sectionId: "promo"));

        var copy = Section(editor, 3);
        Assert.Equal("sc1", Text(copy["componentRef"]));
        Assert.Equal(3, copy.Count);
        Assert.NotEqual("promo", Text(copy["id"]));
    }

    [Fact]
    public async Task MoveSection_ToAPosition()
    {
        var editor = await EditAsync(new PageEditOperation { Op = "move_section", SectionId = "note", Position = 0 });

        Assert.Equal(["note", "hero", "list", "promo"], Ids(editor));
    }

    [Fact]
    public async Task MoveSection_AfterAnother()
    {
        var editor = await EditAsync(new PageEditOperation { Op = "move_section", SectionId = "hero", After = "promo" });

        Assert.Equal(["list", "promo", "hero", "note"], Ids(editor));
    }

    [Fact]
    public async Task MoveSection_WithoutAPlace_IsRefused()
    {
        Assert.Contains("Pass position, after or before", await RefusedAsync(Op("move_section", sectionId: "hero")));
    }

    [Fact]
    public async Task MoveSection_NextToItself_IsRefused()
    {
        Assert.Contains("next to itself", await RefusedAsync(new PageEditOperation { Op = "move_section", SectionId = "hero", Before = "hero" }));
    }

    [Fact]
    public async Task HideAndShowSection_SetTheHiddenFlag()
    {
        var hidden = await EditAsync(Op("hide_section", sectionId: "hero"));
        var shown = await EditAsync(Op("hide_section", sectionId: "hero"), Op("show_section", sectionId: "hero"));

        Assert.True(Section(hidden, 0)["hidden"]!.GetValue<bool>());
        Assert.False(Section(shown, 0)["hidden"]!.GetValue<bool>());
    }

    [Fact]
    public async Task HideSection_OfAPlacement_IsRefused()
    {
        Assert.Contains("placed Shared Component", await RefusedAsync(Op("hide_section", sectionId: "promo")));
    }

    [Fact]
    public async Task RemoveSection_TakesItOut()
    {
        var editor = await EditAsync(Op("remove_section", sectionId: "list"));

        Assert.Equal(["hero", "promo", "note"], Ids(editor));
    }

    [Fact]
    public async Task AnUnknownSection_IsRefused_WithWhereToFindTheIds()
    {
        var error = await RefusedAsync(Op("remove_section", sectionId: "missing"));

        Assert.Contains("no section 'missing'", error);
        Assert.Contains("search_site_pages", error);
    }

    [Fact]
    public async Task AnUnknownOperation_IsRefused_WithTheKnownOnes()
    {
        Assert.Contains("add_section", await RefusedAsync(Op("rename_page")));
    }

    [Fact]
    public async Task SetSectionSettings_ChangesTheValuesNamed_AndKeepsTheOthers()
    {
        var editor = await EditAsync(Op("set_section_settings", sectionId: "hero",
            settings: new() { ["title"] = "Summer", ["button.url"] = "/summer", ["subtitle"] = "New" }));

        var hero = Section(editor, 0);
        Assert.Equal("Summer", Text(hero["title"]));
        Assert.Equal("/summer", Text(hero["button"]!["url"]));
        Assert.Equal("Shop", Text(hero["button"]!["text"]));
        Assert.Equal("New", Text(hero["subtitle"]));
    }

    [Fact]
    public async Task SetSectionSettings_Null_RemovesTheSetting()
    {
        var editor = await EditAsync(Op("set_section_settings", sectionId: "hero", settings: new() { ["button.text"] = null }));

        Assert.False(Section(editor, 0)["button"]!.AsObject().ContainsKey("text"));
    }

    [Fact]
    public async Task SetSectionSettings_OfABlock_ByBlockIdOrByPath()
    {
        var editor = await EditAsync(
            Op("set_section_settings", sectionId: "list", blockId: "b1", settings: new() { ["label"] = "First" }),
            Op("set_section_settings", sectionId: "list", settings: new() { ["blocks.b2.label"] = "Second" }));

        Assert.Equal(["First", "Second"], Section(editor, 1)["blocks"]!.AsArray().Select(x => Text(x!["label"])));
    }

    [Theory]
    [InlineData("id", "identifies the item")]
    [InlineData("type", "identifies the item")]
    [InlineData("hidden", "hide_section")]
    [InlineData("blocks", "their own operations")]
    [InlineData("blocks.b1.id", "identifies the item")]
    public async Task SetSectionSettings_OfWhatTheItemIs_IsRefused(string path, string reason)
    {
        Assert.Contains(reason, await RefusedAsync(Op("set_section_settings", sectionId: "list", settings: new() { [path] = "x" })));
    }

    [Fact]
    public async Task SetSectionSettings_OfAPlacement_IsRefused()
    {
        Assert.Contains("revise_shared_component", await RefusedAsync(Op("set_section_settings", sectionId: "promo", settings: new() { ["title"] = "x" })));
    }

    [Fact]
    public async Task ATextPerLanguage_ChangesInTheLanguageNamedOnly()
    {
        var editor = await EditAsync(Op("set_section_settings", sectionId: "note", settings: new() { ["text"] = "Salut" }, language: "fr-fr"));

        var text = Section(editor, 3)["text"]!.AsObject();
        Assert.Equal("Hello", Text(text["en-US"]));
        Assert.Equal("Salut", Text(text["fr-FR"]));
    }

    [Fact]
    public async Task ATextPerLanguage_GetsANewLanguage()
    {
        var editor = await EditAsync(Op("set_section_settings", sectionId: "note", settings: new() { ["text"] = "Hallo" }, language: "de-DE"));

        Assert.Equal(["en-US", "fr-FR", "de-DE"], Section(editor, 3)["text"]!.AsObject().Select(x => x.Key));
    }

    [Fact]
    public async Task ATextPerLanguage_IsNotReplacedByOneText()
    {
        Assert.Contains("pass language", await RefusedAsync(Op("set_section_settings", sectionId: "note", settings: new() { ["text"] = "Hi" })));
    }

    [Fact]
    public async Task ALanguage_ForOneTextForEveryLanguage_IsRefused()
    {
        Assert.Contains("language does not apply",
            await RefusedAsync(Op("set_section_settings", sectionId: "hero", settings: new() { ["title"] = "Été" }, language: "fr-FR")));
    }

    [Fact]
    public async Task Blocks_AreAddedMovedDuplicatedHiddenAndRemoved()
    {
        var editor = await EditAsync(
            new PageEditOperation { Op = "add_block", SectionId = "list", Type = "item", Settings = new() { ["label"] = "Zero" }, Position = 0 },
            new PageEditOperation { Op = "move_block", SectionId = "list", BlockId = "b2", Before = "b1" },
            Op("duplicate_block", sectionId: "list", blockId: "b1"),
            Op("hide_block", sectionId: "list", blockId: "b2"),
            Op("remove_block", sectionId: "list", blockId: "b1"));

        var blocks = Section(editor, 1)["blocks"]!.AsArray();
        Assert.Equal(["Zero", "Two", "One"], blocks.Select(x => Text(x!["label"])));
        Assert.True(blocks[1]!["hidden"]!.GetValue<bool>());
        Assert.NotEqual("b1", Text(blocks[2]!["id"]));
    }

    [Fact]
    public async Task AddBlock_ToASectionWithoutBlocks_StartsItsList()
    {
        var editor = await EditAsync(new PageEditOperation { Op = "add_block", SectionId = "hero", Type = "badge" });

        Assert.Equal("badge", Text(Section(editor, 0)["blocks"]![0]!["type"]));
    }

    [Fact]
    public async Task AnUnknownBlock_IsRefused()
    {
        Assert.Contains("no block 'b9'", await RefusedAsync(Op("remove_block", sectionId: "list", blockId: "b9")));
    }

    [Fact]
    public async Task SetPageSettings_ChangesTheSeoAndLayout()
    {
        var editor = await EditAsync(Op("set_page_settings", settings: new() { ["seoInfo.pageTitle"] = "Spring sale", ["layout"] = "full" }));

        Assert.Equal("Spring sale", Text(editor.Settings["seoInfo"]!["pageTitle"]));
        Assert.Equal("full", Text(editor.Settings["layout"]));
        Assert.Equal("Home", Text(editor.Settings["name"]));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("permalink")]
    [InlineData("cultureName")]
    public async Task SetPageSettings_OfThePagesOwnNameAddressOrLanguage_IsRefused(string key)
    {
        Assert.Contains("does not change", await RefusedAsync(Op("set_page_settings", settings: new() { [key] = "x" })));
    }

    [Fact]
    public async Task TheOperations_ApplyInOrder()
    {
        var editor = await EditAsync(
            new PageEditOperation { Op = "move_section", SectionId = "note", Position = 0 },
            Op("remove_section", sectionId: "hero"),
            new PageEditOperation { Op = "move_section", SectionId = "promo", After = "note" });

        Assert.Equal(["note", "promo", "list"], Ids(editor));
    }

    [Fact]
    public async Task AFailedOperation_NamesItsNumber()
    {
        var error = await RefusedAsync(Op("hide_section", sectionId: "hero"), Op("remove_section", sectionId: "hero"), Op("hide_section", sectionId: "hero"));

        Assert.StartsWith("Operation 3 (hide_section): The page has no section 'hero'.", error);
    }

    [Fact]
    public async Task CopySectionsFrom_CopiesThePickedSectionsWithNewIds()
    {
        _sources.Pages["other"] = SiteBuilderPageDocument.Read("""
            { "settings": {}, "content": [ { "id": "s1", "type": "faq", "blocks": [ { "id": "q1", "type": "question" } ] }, { "id": "s2", "type": "cta" }, { "id": "s3", "type": "text" } ] }
            """)!.Sections;

        var editor = await EditAsync(new PageEditOperation { Op = "copy_sections_from", SourcePageId = "other", SectionIds = ["s3", "s1"], After = "hero" });

        Assert.Equal(["hero-banner", "faq", "text", "features"], editor.Sections.Take(4).Select(x => Text(x!["type"])));
        Assert.DoesNotContain(Ids(editor), x => x is "s1" or "s3");
        Assert.NotEqual("q1", Text(Section(editor, 1)["blocks"]![0]!["id"]));
    }

    [Fact]
    public async Task CopySectionsFrom_ASectionThePageLacks_IsRefused()
    {
        _sources.Pages["other"] = SiteBuilderPageDocument.Read("""{ "settings": {}, "content": [ { "id": "s1", "type": "faq" } ] }""")!.Sections;

        Assert.Contains("has no section 's9'", await RefusedAsync(new PageEditOperation { Op = "copy_sections_from", SourcePageId = "other", SectionIds = ["s9"] }));
    }

    [Fact]
    public async Task PlaceSharedComponent_AddsAPlacement()
    {
        var editor = await EditAsync(new PageEditOperation { Op = "place_shared_component", SharedComponentId = "sc2", Position = 0 });

        var placement = Section(editor, 0);
        Assert.Equal(["id", "type", "componentRef"], placement.Select(x => x.Key));
        Assert.Equal("sc2", Text(placement["componentRef"]));
        Assert.Equal(["sc2"], _sources.CheckedComponents);
    }

    [Fact]
    public async Task PlaceSharedComponent_OnAPageInTheOlderFormat_IsRefused()
    {
        var editor = PageDocumentEditor.Read("""[ { "name": "Old" }, { "id": "a", "type": "text" } ]""")!;

        var error = await Assert.ThrowsAsync<McpException>(() => editor.ApplyAsync(
            [new PageEditOperation { Op = "place_shared_component", SharedComponentId = "sc2" }], _sources, CancellationToken.None));

        Assert.Contains("older format", error.Message);
        Assert.Empty(_sources.CheckedComponents);
    }

    [Fact]
    public async Task DetachSharedComponent_PutsCopiesOfItsSectionsInItsPlace()
    {
        _sources.Components["sc1"] = SiteBuilderPageDocument.Read("""{ "settings": {}, "content": [ { "id": "c1", "type": "banner" }, { "id": "c2", "type": "text" } ] }""")!.Sections;

        var editor = await EditAsync(Op("detach_shared_component", sectionId: "promo"));

        Assert.Equal(["hero-banner", "features", "banner", "text", "notice"], editor.Sections.Select(x => Text(x!["type"])));
        Assert.DoesNotContain(Ids(editor), x => x is "promo" or "c1" or "c2");
    }

    [Fact]
    public async Task DetachSharedComponent_OfASection_IsRefused()
    {
        Assert.Contains("not a placed Shared Component", await RefusedAsync(Op("detach_shared_component", sectionId: "hero")));
    }

    [Fact]
    public async Task APageInTheOlderFormat_IsWrittenInIt()
    {
        var editor = PageDocumentEditor.Read("""[ { "name": "Old" }, { "id": "a", "type": "text" } ]""")!;

        await editor.ApplyAsync([Op("hide_section", sectionId: "a")], _sources, CancellationToken.None);

        var written = JsonNode.Parse(editor.Write())!.AsArray();
        Assert.Equal("Old", Text(written[0]!["name"]));
        Assert.True(written[1]!["hidden"]!.GetValue<bool>());
    }

    [Fact]
    public void ADocument_KeepsWhatItHoldsBesideItsSettingsAndSections()
    {
        var editor = PageDocumentEditor.Read("""{ "version": 2, "settings": {}, "content": [] }""")!;

        Assert.Equal(2, JsonNode.Parse(editor.Write())!["version"]!.GetValue<int>());
    }

    [Fact]
    public void SectionsWithoutAnId_GetTheSameIdEachTimeTheVersionIsRead()
    {
        const string content = """{ "settings": {}, "content": [ { "type": "hero" }, { "type": "text", "blocks": [ { "type": "item" } ] }, { "id": "hero1", "type": "x" } ] }""";

        var first = SiteBuilderPageDocument.Read(content)!.Sections;
        var second = PageDocumentEditor.Read(content)!.Sections;

        Assert.Equal(first.Select(x => Text(x!["id"])), second.Select(x => Text(x!["id"])));
        Assert.Equal(3, first.Select(x => Text(x!["id"])).Distinct().Count());
        Assert.NotNull(Text(first[1]!["blocks"]![0]!["id"]));
    }

    [Fact]
    public void TextsAreWrittenAsTheyAre()
    {
        var editor = PageDocumentEditor.Read("""{ "settings": {}, "content": [ { "id": "a", "type": "text", "body": "<p>Été & co</p>" } ] }""")!;

        Assert.Contains("<p>Été & co</p>", editor.Write());
    }

    [Fact]
    public async Task TheChanges_AreListedSectionBySection()
    {
        var before = PageDocumentEditor.Read(Document)!;
        var after = await EditAsync(
            Op("set_section_settings", sectionId: "hero", settings: new() { ["title"] = "Summer" }),
            Op("hide_section", sectionId: "list"),
            Op("remove_section", sectionId: "note"),
            new PageEditOperation { Op = "move_section", SectionId = "promo", Position = 0 },
            new PageEditOperation { Op = "add_section", Type = "text", Settings = new() { ["body"] = "Hi" } },
            Op("set_section_settings", sectionId: "list", blockId: "b1", settings: new() { ["label"] = "First" }),
            Op("set_page_settings", settings: new() { ["seoInfo.pageTitle"] = "New title" }));
        var added = Text(Section(after, 3)["id"]);

        var changes = PageDocumentChanges.Compare(before, after);

        Assert.Contains(new PageDocumentChange("page settings: seoInfo.pageTitle", "Old title", "New title"), changes);
        Assert.Contains(new PageDocumentChange("section notice 'note'", "at position 3", "removed"), changes);
        Assert.Contains(changes, x => x.Field == "order of sections" && x.To.StartsWith("Shared Component 'sc1'"));
        Assert.Contains(new PageDocumentChange("section hero-banner 'hero': title", "Spring", "Summer"), changes);
        Assert.Contains(new PageDocumentChange("section features 'list' hidden", "false", "true"), changes);
        Assert.Contains(new PageDocumentChange("section features 'list' block item 'b1': label", "One", "First"), changes);
        Assert.Contains(new PageDocumentChange($"section text '{added}'", null, "added at position 3"), changes);
        Assert.Contains(new PageDocumentChange($"section text '{added}': body", null, "Hi"), changes);
    }

    private async Task<PageDocumentEditor> EditAsync(params PageEditOperation[] operations)
    {
        var editor = PageDocumentEditor.Read(Document)!;
        await editor.ApplyAsync(operations, _sources, CancellationToken.None);
        return editor;
    }

    private async Task<string> RefusedAsync(params PageEditOperation[] operations)
    {
        var error = await Assert.ThrowsAsync<McpException>(() => EditAsync(operations));
        return error.Message;
    }

    private static PageEditOperation Op(string op, string sectionId = null, string blockId = null, string type = null,
        Dictionary<string, JsonNode> settings = null, string language = null) => new()
    {
        Op = op,
        SectionId = sectionId,
        BlockId = blockId,
        Type = type,
        Settings = settings,
        Language = language,
    };

    private static JsonObject Section(PageDocumentEditor editor, int index) => editor.Sections[index]!.AsObject();

    private static List<string> Ids(PageDocumentEditor editor) => editor.Sections.Select(x => Text(x!["id"])).ToList();

    private static string Text(JsonNode node) => SiteBuilderPageItems.GetString(node);

    private sealed class FakeSources : IPageEditSources
    {
        public Dictionary<string, JsonArray> Pages { get; } = [];

        public Dictionary<string, JsonArray> Components { get; } = [];

        public List<string> CheckedComponents { get; } = [];

        public Task<JsonArray> GetPageSectionsAsync(string pageId, CancellationToken cancellationToken) =>
            Task.FromResult(Pages.TryGetValue(pageId, out var sections) ? sections : throw new McpException("not found"));

        public Task EnsureSharedComponentAsync(string sharedComponentId, CancellationToken cancellationToken)
        {
            CheckedComponents.Add(sharedComponentId);
            return Task.CompletedTask;
        }

        public Task<JsonArray> GetSharedComponentSectionsAsync(string sharedComponentId, CancellationToken cancellationToken) =>
            Task.FromResult(Components.TryGetValue(sharedComponentId, out var sections) ? sections : throw new McpForbiddenException("no"));
    }
}
