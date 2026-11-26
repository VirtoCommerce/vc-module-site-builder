using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP.Services;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class DiscardDraftToolTests : IAsyncDisposable
{
    private const string Tool = "discard_draft";

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;

    public DiscardDraftToolTests()
    {
        _pages = _context.UsePageStore();
        _context.SeedPage("edited", StoreA, published: true, draft: true);
        _context.SeedPage("new", StoreA, published: false, draft: true);
        _context.SeedPage("b1", StoreB, published: true, draft: true);
        _context.ListPages(() => []);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public void TheTool_NeedsBuilderReadAndUpdate()
    {
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), Editor()));
        Assert.False(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), Reader()));
    }

    private static Dictionary<string, object> Page(string pageId) => new() { ["kind"] = "page", ["page_id"] = pageId };

    private static Dictionary<string, object> Palette() => new() { ["kind"] = "theme_file", ["store_id"] = StoreA, ["theme_file"] = "default/config/palette.json" };

    [Fact]
    public async Task AConfirmedDiscard_DropsTheDraft_AndShowsWhatItUndid()
    {
        var result = await _context.CallAsync(Editor(), Tool, await _context.ReviewAsync(Editor(), Tool, Page("edited")));

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        Assert.True(result.AskedForConfirmation);
        Assert.Contains(change.Changes, x => x.Field == "section hero-banner 'hero': title" && x.From?.ToString() == "Summer" && x.To?.ToString() == "Spring");
        Assert.Equal(["Published"], (await _pages.GetByIdAsync("edited")).Pages.Select(x => x.Status));
    }

    [Fact]
    public async Task ADeclinedDiscard_DiscardsNothing()
    {
        var result = await _context.CallAsync(Editor(), Tool, await _context.ReviewAsync(Editor(), Tool, Page("edited")),
            new McpToolCallOptions { DeclineConfirmation = true });

        Assert.Equal(McpToolCallOutcome.Declined, result.Outcome);
        Assert.Contains((await _pages.GetByIdAsync("edited")).Pages, x => x.Status == "Draft");
    }

    [Fact]
    public async Task ADryRun_DoesNotAsk_AndDiscardsNothing()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { kind = "page", page_id = "edited", dryRun = true });

        Assert.False(result.AskedForConfirmation);
        Assert.True(result.EnsureSucceeded().Read<SiteBuilderChange>().DryRun);
        Assert.Contains((await _pages.GetByIdAsync("edited")).Pages, x => x.Status == "Draft");
    }

    [Fact]
    public async Task ADiscardWithoutTheReviewedVersion_IsRefused()
    {
        var result = await _context.CallAsync(Editor(), Tool, Page("edited"));

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("expected_versions", result.Error, StringComparison.Ordinal);
        Assert.Contains((await _pages.GetByIdAsync("edited")).Pages, x => x.Status == "Draft");
    }

    [Fact]
    public async Task ADiscardAfterAColleagueSavedTheDraft_IsRefused_AndKeepsTheirWork()
    {
        var call = await _context.ReviewAsync(Editor(), Tool, Page("edited"));
        _pages.SeedContent("edited-draft", SampleContent.Replace("Spring", "Autumn", StringComparison.Ordinal));

        var result = await _context.CallAsync(Editor(), Tool, call);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error, StringComparison.Ordinal);
        Assert.Contains((await _pages.GetByIdAsync("edited")).Pages, x => x.Status == "Draft");
    }

    [Fact]
    public async Task ADraftNeverPublished_IsNotDiscarded()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { kind = "page", page_id = "new", expected_versions = new Dictionary<string, string>() });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("never published", result.Error);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("new")).Status);
    }

    [Fact]
    public async Task APageOfAnotherStore_IsForbidden()
    {
        var result = await _context.CallAsync(StoreAEditor(), Tool, new { kind = "page", page_id = "b1", expected_versions = new Dictionary<string, string>() });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Contains((await _pages.GetByIdAsync("b1")).Pages, x => x.Status == "Draft");
    }

    [Fact]
    public async Task AnotherKind_IsRefused()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { kind = "shared_component", page_id = "edited" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("kind must be 'page' or 'theme_file'", result.Error, StringComparison.Ordinal);
        Assert.Contains((await _pages.GetByIdAsync("edited")).Pages, x => x.Status == "Draft");
    }

    private static McpTestUser ThemeEditor() => McpTestUser.Create("theme-editor").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Theme);

    private async Task SeedPaletteAsync()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "config/palette.json", """{ "color_primary_500": "#0a5f38" }""");
        _context.SeedThemeFile(StoreA, "config/palette.json", """{ "color_primary_500": "#ff0000" }""", draft: true);
    }

    [Fact]
    public async Task AThemeFile_IsDiscardedAfterConfirmation_ShowingWhatGoesBack()
    {
        await SeedPaletteAsync();

        var result = await _context.CallAsync(ThemeEditor(), Tool, await _context.ReviewAsync(ThemeEditor(), Tool, Palette()));

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        Assert.True(result.AskedForConfirmation);
        Assert.Contains(change.Changes, x => x.Field == "color_primary_500" && x.From?.ToString() == "#ff0000" && x.To?.ToString() == "#0a5f38");
        Assert.Null(_context.ReadThemeFile(StoreA, "config/palette.json", draft: true));
    }

    [Fact]
    public async Task AThemeFileDryRun_DiscardsNothing()
    {
        await SeedPaletteAsync();

        var result = await _context.CallAsync(ThemeEditor(), Tool, new { kind = "theme_file", store_id = StoreA, theme_file = "default/config/palette.json", dryRun = true });

        Assert.True(result.EnsureSucceeded().Read<SiteBuilderChange>().DryRun);
        Assert.NotNull(_context.ReadThemeFile(StoreA, "config/palette.json", draft: true));
    }

    [Fact]
    public async Task ADeclinedThemeFileDiscard_DiscardsNothing()
    {
        await SeedPaletteAsync();

        var result = await _context.CallAsync(ThemeEditor(), Tool, await _context.ReviewAsync(ThemeEditor(), Tool, Palette()),
            new McpToolCallOptions { DeclineConfirmation = true });

        Assert.Equal(McpToolCallOutcome.Declined, result.Outcome);
        Assert.NotNull(_context.ReadThemeFile(StoreA, "config/palette.json", draft: true));
    }

    [Fact]
    public async Task AThemeFileSavedAgainAfterTheReview_IsNotDiscarded()
    {
        await SeedPaletteAsync();
        var call = await _context.ReviewAsync(ThemeEditor(), Tool, Palette());
        _context.SeedThemeFile(StoreA, "config/palette.json", """{ "color_primary_500": "#00ff00" }""", draft: true);

        var result = await _context.CallAsync(ThemeEditor(), Tool, call);
        var unpinned = await _context.CallAsync(ThemeEditor(), Tool, Palette());

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the dry run", result.Error, StringComparison.Ordinal);
        Assert.Equal(McpToolCallOutcome.Failed, unpinned.Outcome);
        Assert.Contains("#00ff00", _context.ReadThemeFile(StoreA, "config/palette.json", draft: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThemeFile_NeedsThePermissionOfItsPart()
    {
        await SeedPaletteAsync();

        var result = await _context.CallAsync(Editor(), Tool, new { kind = "theme_file", store_id = StoreA, theme_file = "default/config/palette.json" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Contains("builder:theme", result.Error, StringComparison.Ordinal);
        Assert.NotNull(_context.ReadThemeFile(StoreA, "config/palette.json", draft: true));
    }

    [Fact]
    public async Task AThemeFileNeverPublished_IsNotDiscarded()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "templates/product.new.json", ProductTemplate, draft: true);

        var result = await _context.CallAsync(McpTestUser.Create("t").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Templates), Tool,
            new { kind = "theme_file", store_id = StoreA, theme_file = "default/templates/product.new.json" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("never published", result.Error, StringComparison.Ordinal);
        Assert.NotNull(_context.ReadThemeFile(StoreA, "templates/product.new.json", draft: true));
    }

    [Fact]
    public async Task ACustomSectionTypeNeverPublished_IsRemoved_AndTheChangeSaysSo()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", McpTestContext.StatsSectionType, draft: true);
        var type = new Dictionary<string, object> { ["kind"] = "theme_file", ["store_id"] = StoreA, ["theme_file"] = "default/custom-sections/custom-stats.json" };

        var result = await _context.CallAsync(ThemeEditor(), Tool, await _context.ReviewAsync(ThemeEditor(), Tool, type));

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        var removed = Assert.Single(change.Changes);
        Assert.Equal(("section type", "custom-stats"), (removed.Field, removed.From?.ToString()));
        Assert.StartsWith("removed", removed.To?.ToString(), StringComparison.Ordinal);
        Assert.Contains(change.Warnings, x => x.Contains("A page that places it shows nothing in its place", StringComparison.Ordinal) &&
            x.Contains("a template draft that places it blocks publish_theme until the section is removed", StringComparison.Ordinal));
        Assert.Null(_context.ReadThemeFile(StoreA, "custom-sections/custom-stats.json", draft: true));
    }

    [Fact]
    public async Task TheDryRunOfACustomSectionTypeNeverPublished_SaysByNameThatItIsDeletedForGood_AndHowManyPagesPlaceIt()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", McpTestContext.StatsSectionType, draft: true);
        var spring = _context.SeedDraft("spring", StoreA, """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""");
        var about = _context.SeedPage("about", StoreA, published: true, draft: false,
            content: """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" }, { "id": "s2", "type": "custom-stats" } ] }""");
        var plain = _context.SeedDraft("plain", StoreA, """{ "settings": {}, "content": [ { "id": "t1", "type": "text" } ] }""");
        _context.ListPages(() => [LandingRow(spring), LandingRow(about), LandingRow(plain)]);

        var result = await _context.CallAsync(ThemeEditor(), Tool,
            new { kind = "theme_file", store_id = StoreA, theme_file = "default/custom-sections/custom-stats.json", dryRun = true });

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        Assert.Equal("Section type 'Stats' (custom-stats) is deleted for good: an AI agent created it for this store, it was never published, and " +
            "its draft is its only copy, so it cannot be restored. 2 pages place it and will show nothing in its place: 'Page about' (/about) and " +
            "'Page spring' (/spring).", change.Warnings[0]);
        Assert.Equal("removed for good: it was never published, so its draft is its only copy and cannot be restored",
            Assert.Single(change.Changes, x => x.Field == "section type").To?.ToString());
        Assert.NotNull(_context.ReadThemeFile(StoreA, "custom-sections/custom-stats.json", draft: true));
    }

    private const string PlacesStats = """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""";

    private async Task<string> DeletionWarningAsync(Func<IEnumerable<SiteBuilderPageListItem>> rows, string sectionType = StatsSectionType)
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", sectionType, draft: true);
        _context.ListPages(rows);

        var result = await _context.CallAsync(ThemeEditor(), Tool,
            new { kind = "theme_file", store_id = StoreA, theme_file = "default/custom-sections/custom-stats.json", dryRun = true });

        return Assert.Single(result.EnsureSucceeded().Read<SiteBuilderChange>().Warnings, x => x.Contains("for good", StringComparison.Ordinal));
    }

    private List<SiteBuilderPageListItem> SeedPages(int count, int placing) =>
        Enumerable.Range(1, count)
            .Select(i => LandingRow(_context.SeedDraft($"p{i:00}", StoreA, i <= placing ? PlacesStats : """{ "settings": {}, "content": [] }""")))
            .ToList();

    [Fact]
    public async Task TheDeletionWarning_NamesTenPlacingPages_AndCountsTheRest()
    {
        var rows = SeedPages(12, placing: 12);

        var warning = await DeletionWarningAsync(() => rows);

        Assert.EndsWith("12 pages place it and will show nothing in its place: 'Page p01' (/p01), 'Page p02' (/p02), 'Page p03' (/p03), " +
            "'Page p04' (/p04), 'Page p05' (/p05), 'Page p06' (/p06), 'Page p07' (/p07), 'Page p08' (/p08), 'Page p09' (/p09), 'Page p10' (/p10) " +
            "and 2 more.", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeletionWarning_NamesASinglePlacingPage_AndSaysWhenNotEveryPageWasRead()
    {
        var rows = SeedPages(51, placing: 1);

        var warning = await DeletionWarningAsync(() => rows);

        Assert.EndsWith("cannot be restored. 1 page places it and will show nothing in its place: 'Page p01' (/p01). Only 50 of the store's 51 " +
            "pages were read, so others may place it too.", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "No page places it. 1 language version could not be read, so it may place it too.")]
    [InlineData(2, "No page places it. 2 language versions could not be read, so they may place it too.")]
    public async Task TheDeletionWarning_SaysHowManyLanguageVersionsCouldNotBeRead(int unread, string expected)
    {
        var rows = Enumerable.Range(1, unread).Select(i => LandingRow(_context.SeedDraft($"empty{i}", StoreA, string.Empty))).ToList();

        var warning = await DeletionWarningAsync(() => rows);

        Assert.EndsWith(expected, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeletionWarning_CountsAMultilanguagePageOnce_ByItsOwnName()
    {
        var english = _context.SeedDraft("about-en", StoreA, PlacesStats, multilanguagePageId: "about");
        var french = _context.SeedDraft("about-fr", StoreA, PlacesStats, "fr-FR", "about");
        SiteBuilderPageListItem[] rows =
        [
            new()
            {
                Type = "Multilanguage",
                Id = "about",
                Name = "About",
                Permalink = "/about",
                MultilanguagePage = Root("about", StoreA),
                Languages = [new MultilanguagePageLanguage { CultureName = "en-US", Page = english }, new MultilanguagePageLanguage { CultureName = "fr-FR", Page = french }],
            },
        ];

        var warning = await DeletionWarningAsync(() => rows);

        Assert.EndsWith("1 page places it and will show nothing in its place: 'About' (/about).", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeletionWarning_NamesATypeNamedPerLanguage_InTheStoresDefaultLanguage()
    {
        var warning = await DeletionWarningAsync(() => [],
            StatsSectionType.Replace("\"name\": \"Stats\"", "\"name\": { \"fr-FR\": \"Statistiques\", \"en-US\": \"Stats band\" }", StringComparison.Ordinal));

        Assert.StartsWith("Section type 'Stats band' (custom-stats) is deleted for good", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTool_TellsTheAgentToShowTheUserThatADiscardDeletesACustomSectionTypeForGood()
    {
        Assert.Contains("show the user the dry run's warnings before the call", _context.Harness.GetTool(Tool).ProtocolTool.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomSectionTypeDraft_IsDiscarded_AndTheChangeListsWhatGoesBackToThePublishedType()
    {
        const string draft = "<section><h2>{{ settings.title }}</h2><p>{{ settings.title }}</p></section>";
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", McpTestContext.StatsSectionType);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json",
            McpTestContext.StatsSectionType.Replace("<section><h2>{{ settings.title }}</h2></section>", draft, StringComparison.Ordinal), draft: true);
        var type = new Dictionary<string, object> { ["kind"] = "theme_file", ["store_id"] = StoreA, ["theme_file"] = "default/custom-sections/custom-stats.json" };

        var result = await _context.CallAsync(ThemeEditor(), Tool, await _context.ReviewAsync(ThemeEditor(), Tool, type));

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        var reverted = Assert.Single(change.Changes);
        Assert.Equal(("template line 1", draft, "<section><h2>{{ settings.title }}</h2></section>"),
            (reverted.Field, reverted.From?.ToString(), reverted.To?.ToString()));
        Assert.Null(_context.ReadThemeFile(StoreA, "custom-sections/custom-stats.json", draft: true));
        Assert.Equal(McpTestContext.StatsSectionType, _context.ReadThemeFile(StoreA, "custom-sections/custom-stats.json"));
    }

    [Fact]
    public async Task AThemeFileOfAnotherStore_IsForbidden()
    {
        await _context.SeedThemeAsync(StoreB);

        var result = await _context.CallAsync(
            McpTestUser.Create("a").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update, Permissions.Theme), Tool,
            new { kind = "theme_file", store_id = StoreB, theme_file = "default/config/palette.json" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }
}
