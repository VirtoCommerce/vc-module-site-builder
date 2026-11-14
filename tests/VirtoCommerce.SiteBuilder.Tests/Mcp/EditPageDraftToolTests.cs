using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.MCP;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class EditPageDraftToolTests : IAsyncDisposable
{
    private const string Tool = "edit_page_draft";
    private const string Content = """{ "settings": { "name": "Home", "seoInfo": { "pageTitle": "Home" } }, "content": [ { "id": "hero", "type": "hero-banner", "title": "Spring" }, { "id": "text", "type": "text" } ] }""";

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;

    public EditPageDraftToolTests()
    {
        _pages = _context.UsePageStore();
        Seed("a1", StoreA);
        Seed("b1", StoreB);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    // builder:update is granted in every store, as the designer's save requires; builder:read may be limited to a store.
    private static McpTestUser Editor(string name = "editor") => McpTestUser.Create(name).WithPermissions(Permissions.Read, Permissions.Update);

    private static McpTestUser StoreAEditor() =>
        McpTestUser.Create("store-a-editor").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update);

    [Fact]
    public void TheTool_IsVisibleWithBuilderReadAndUpdate_Only()
    {
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), Editor()));
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), StoreAEditor()));
        Assert.False(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), Reader()));
        Assert.False(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), McpTestUser.Create("updater").WithPermissions(Permissions.Update)));
    }

    [Fact]
    public async Task ASave_ChangesTheDraft_AndAnswersTheNewVersion()
    {
        var edit = await EditAsync(Editor(), "a1", Token("a1"), [SetTitle("Summer")]);

        var draft = await DraftAsync("a1");
        Assert.False(edit.DryRun);
        Assert.Equal("Summer", SiteBuilderPageItems.GetString(JsonNode.Parse(draft.Content)!["content"]![0]!["title"]));
        Assert.Equal(draft.ETag, edit.VersionToken);
        Assert.Contains(edit.Changes, x => x.Field == "section hero-banner 'hero': title" && x.From?.ToString() == "Spring" && x.To?.ToString() == "Summer");
        Assert.Equal(["hero", "text"], edit.Sections.Select(x => x.Id));
        Assert.Single(_context.SavedVersions.Records);
    }

    [Fact]
    public async Task TheNewVersion_TakesTheNextEdit()
    {
        var first = await EditAsync(Editor(), "a1", Token("a1"), [SetTitle("Summer")]);
        var second = await EditAsync(Editor(), "a1", first.VersionToken, [new { op = "hide_section", section_id = "text" }]);

        Assert.Contains(second.Changes, x => x.Field == "section text 'text' hidden");
    }

    [Fact]
    public async Task ADryRun_WritesNothing_AndShowsTheChange()
    {
        var token = Token("a1");

        var edit = await EditAsync(Editor(), "a1", token, [SetTitle("Summer"), new { op = "add_section", type = "faq", position = 0 }], dryRun: true);

        Assert.True(edit.DryRun);
        Assert.Equal(token, edit.VersionToken);
        Assert.Contains(edit.Changes, x => x.Field == "section hero-banner 'hero': title");
        Assert.Contains(edit.Changes, x => x.Field.StartsWith("section faq '") && x.To?.ToString() == "added at position 0");
        Assert.Equal(Content, (await DraftAsync("a1")).Content);
        Assert.Empty(_context.SavedVersions.Records);
    }

    [Fact]
    public async Task AStaleVersion_IsRefused_AndTheAgentToldToReadAgain()
    {
        var token = Token("a1");
        await EditAsync(Editor(), "a1", token, [SetTitle("Summer")]);

        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "a1", version_token = token, operations = new[] { SetTitle("Autumn") } });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since you read it", result.Error);
        Assert.Contains("search_site_pages", result.Error);
    }

    [Fact]
    public async Task AStaleVersion_IsRefused_OnADryRunToo()
    {
        var result = await _context.CallAsync(Editor(), Tool,
            new { page_id = "a1", version_token = "\"stale\"", operations = new[] { SetTitle("Autumn") }, dryRun = true });

        Assert.Contains("changed since you read it", result.Error);
    }

    [Fact]
    public async Task APageOfAnotherStore_IsRefused()
    {
        var result = await _context.CallAsync(StoreAEditor(), Tool, new { page_id = "b1", version_token = Token("b1"), operations = new[] { SetTitle("Summer") } });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Equal(Content, (await DraftAsync("b1")).Content);
    }

    [Fact]
    public async Task AStoreEditor_EditsAPageOfTheirStore()
    {
        var edit = await EditAsync(StoreAEditor(), "a1", Token("a1"), [SetTitle("Summer")]);

        Assert.NotEmpty(edit.Changes);
    }

    [Fact]
    public async Task AnAccountBoundToAnotherStore_IsRefused()
    {
        _context.BindAccountsTo(StoreB);

        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "a1", version_token = Token("a1"), operations = new[] { SetTitle("Summer") } });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task AnInvalidOperation_IsRefused_AndNothingSaved()
    {
        var result = await _context.CallAsync(Editor(), Tool,
            new { page_id = "a1", version_token = Token("a1"), operations = new object[] { SetTitle("Summer"), new { op = "remove_section", section_id = "missing" } } });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("Operation 2 (remove_section)", result.Error);
        Assert.Equal(Content, (await DraftAsync("a1")).Content);
    }

    [Fact]
    public async Task ContentTheSaveRefuses_IsReportedWithItsReason()
    {
        var result = await _context.CallAsync(Editor(), Tool, new
        {
            page_id = "a1",
            version_token = Token("a1"),
            operations = new[] { new { op = "set_section_settings", section_id = "hero", settings = new { link = "javascript:alert(1)" } } },
        });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("was not saved", result.Error);
        Assert.Contains("content[0].link", result.Error);
        Assert.Equal(Content, (await DraftAsync("a1")).Content);
    }

    [Fact]
    public async Task CopyingFromAPageOfAnotherStore_TheUserDoesNotRead_IsRefused()
    {
        var result = await _context.CallAsync(StoreAEditor(), Tool,
            new { page_id = "a1", version_token = Token("a1"), operations = new[] { new { op = "copy_sections_from", source_page_id = "b1" } } });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task CopyingFromAnotherPage_AddsItsSections()
    {
        Seed("a2", StoreA, """{ "settings": {}, "content": [ { "id": "faq", "type": "faq" } ] }""");

        var edit = await EditAsync(Editor(), "a1", Token("a1"), [new { op = "copy_sections_from", source_page_id = "a2", after = "hero" }]);

        Assert.Equal(["hero-banner", "faq", "text"], edit.Sections.Select(x => x.Type));
    }

    [Fact]
    public async Task PlacingASharedComponent_NeedsSharedComponentsRead()
    {
        _context.SharedComponents
            .Setup(x => x.GetAsync(It.IsAny<System.Collections.Generic.IList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync([new SiteBuilderSharedComponent { Id = "sc1", StoreId = StoreA, Name = "Banner" }]);
        var withComponents = McpTestUser.Create("components").WithPermissions(Permissions.Read, Permissions.Update, Permissions.SharedComponentsRead);
        var operations = new[] { new { op = "place_shared_component", shared_component_id = "sc1" } };

        var refused = await _context.CallAsync(Editor(), Tool, new { page_id = "a1", version_token = Token("a1"), operations });
        var placed = await EditAsync(withComponents, "a1", Token("a1"), operations);

        Assert.Equal(McpToolCallOutcome.Forbidden, refused.Outcome);
        Assert.Equal("sc1", placed.Sections.Last().SharedComponentId);
    }

    [Fact]
    public async Task AnEditThatChangesNothing_SavesNothing()
    {
        var edit = await EditAsync(Editor(), "a1", Token("a1"), [SetTitle("Spring")]);

        Assert.Empty(edit.Changes);
        Assert.Contains(edit.Warnings, x => x.Contains("nothing was saved"));
        Assert.Empty(_context.SavedVersions.Records);
    }

    [Fact]
    public async Task ThePagesOwnTemplate_IsSet_WhenTheThemeOrTheStorefrontHasIt()
    {
        _context.SeedThemeFile(StoreA, "templates/page.landing.json", ProductTemplate);

        var own = await EditAsync(Editor(), "a1", Token("a1"), [SetTemplate("page.landing")]);
        var builtIn = await EditAsync(Editor(), "a1", own.VersionToken, [SetTemplate("page")]);

        Assert.Contains(own.Changes, x => x.Field.Contains("template", StringComparison.Ordinal) && x.To?.ToString() == "page.landing");
        Assert.Contains(builtIn.Changes, x => x.Field.Contains("template", StringComparison.Ordinal) && x.To?.ToString() == "page");
    }

    [Theory]
    [InlineData("page.missing")]
    [InlineData("product")]
    public async Task ATemplateThatIsNotAPageTemplateOfTheStore_IsRefused(string template)
    {
        _context.SeedThemeFile(StoreA, "templates/page.landing.json", ProductTemplate);

        var result = await _context.CallAsync(Editor(), Tool, new { page_id = "a1", version_token = Token("a1"), operations = new[] { SetTemplate(template) } });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains($"no page template '{template}'", result.Error, StringComparison.Ordinal);
        Assert.Contains("page.landing", result.Error, StringComparison.Ordinal);
        Assert.Equal(Content, (await DraftAsync("a1")).Content);
    }

    [Fact]
    public async Task ATemplateOnlyInTheThemeDraft_IsSet_WithAWarning()
    {
        _context.SeedThemeFile(StoreA, "templates/page.new.json", ProductTemplate, draft: true);

        var edit = await EditAsync(Editor(), "a1", Token("a1"), [SetTemplate("page.new")]);

        Assert.Contains(edit.Warnings, x => x.Contains("only in the theme's draft", StringComparison.Ordinal));
    }

    private async Task<PageDraftEdit> EditAsync(McpTestUser user, string pageId, string token, object[] operations, bool dryRun = false)
    {
        var result = await _context.CallAsync(user, Tool, new { page_id = pageId, version_token = token, operations, dryRun });
        return result.EnsureSucceeded().Read<PageDraftEdit>();
    }

    private static object SetTemplate(string template) => new { op = "set_page_settings", settings = new { template } };

    private static object SetTitle(string title) => new { op = "set_section_settings", section_id = "hero", settings = new { title } };

    private void Seed(string id, string storeId, string content = Content)
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = id,
            StoreId = storeId,
            Name = $"Page {id}",
            Permalink = $"/{id}",
            CultureName = "en-US",
            Pages = [new SiteBuilderPage { Id = $"{id}-draft", GroupId = id, StoreId = storeId, Status = "Draft" }],
        });
        _pages.SeedContent($"{id}-draft", content);
    }

    private Task<SiteBuilderContentSnapshot> DraftAsync(string id) =>
        _pages.LoadGroupContentAsync(_pages.GetByIdAsync(id).GetAwaiter().GetResult(), cancellationToken: CancellationToken.None);

    private string Token(string id) => DraftAsync(id).GetAwaiter().GetResult().ETag;
}
