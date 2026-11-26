using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class PublishThemeToolTests : IAsyncDisposable
{
    private const string Tool = "publish_theme";

    private readonly McpTestContext _context = new();

    public PublishThemeToolTests()
    {
        _context.UsePageStore();
        _context.ListPages(() => []);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private async Task<string> SeedAsync()
    {
        var catalog = await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "templates/product.bicycles.json", ProductTemplate, draft: true);

        return catalog;
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
    public async Task TheDryRun_ListsACustomSectionTypeDraft_AmongTheDrafts()
    {
        await SeedAsync();
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);

        var preview = await DryRunAsync();

        Assert.Equal(["default/custom-sections/custom-stats.json", "default/templates/product.bicycles.json"], preview.Drafts.Select(x => x.Path).Order(StringComparer.Ordinal));
        Assert.Contains(preview.Changes, x => x.Field == "default/custom-sections/custom-stats.json" && x.To?.ToString() == "published");
    }

    [Fact]
    public async Task TheDryRun_OfACustomSectionTypeDraft_ShowsWhatChangesAndThePagesThatPlaceIt()
    {
        await SeedAsync();
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json",
            StatsSectionType.Replace("\"css\": \"\"", "\"css\": \":scope { padding: 2rem; }\"", StringComparison.Ordinal), draft: true);
        var page = new GroupedSiteBuilderPage
        {
            Id = "spring",
            StoreId = StoreA,
            Name = "Spring's\u2028sale",
            Permalink = "/spring",
            CultureName = "en-US",
            Visibility = true,
            Pages =
            [
                new SiteBuilderPage { Id = "spring-published", GroupId = "spring", StoreId = StoreA, Status = "Published" },
                new SiteBuilderPage { Id = "spring-draft", GroupId = "spring", StoreId = StoreA, Status = "Draft" },
            ],
        };
        _context.PageStore.SeedGroup(page);
        _context.PageStore.SeedContent("spring-published", """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""");
        // only the published version places the type here: the draft places no section of it, so the page is listed once
        _context.PageStore.SeedContent("spring-draft", """{ "settings": {}, "content": [ { "id": "t1", "type": "text" } ] }""");
        _context.ListPages(() => [LandingRow(page)]);

        var preview = await DryRunAsync();

        Assert.Contains(preview.Changes, x => x.Field == "default/custom-sections/custom-stats.json css line 1" && x.To?.ToString() == ":scope { padding: 2rem; }");
        var placed = preview.Drafts.Single(x => x.Path == "default/custom-sections/custom-stats.json").PlacedSections!;
        Assert.Equal([("spring", "Spring''s sale", 1)], placed.Pages.Select(x => (x.PageId, x.Name, x.Sections)));
        Assert.Contains("Templates are not scanned", placed.Coverage, StringComparison.Ordinal);
        Assert.Null(preview.Drafts.Single(x => x.Path == "default/templates/product.bicycles.json").PlacedSections);
    }

    [Fact]
    public async Task TheDryRun_OfACustomSectionTypeDraft_AlsoListsThePageDraftsThatPlaceIt()
    {
        await SeedAsync();
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        var published = new GroupedSiteBuilderPage
        {
            Id = "spring",
            StoreId = StoreA,
            Name = "Spring",
            Permalink = "/spring",
            CultureName = "en-US",
            Pages =
            [
                new SiteBuilderPage { Id = "spring-published", GroupId = "spring", StoreId = StoreA, Status = "Published" },
                new SiteBuilderPage { Id = "spring-draft", GroupId = "spring", StoreId = StoreA, Status = "Draft" },
            ],
        };
        _context.PageStore.SeedGroup(published);
        _context.PageStore.SeedContent("spring-published", """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""");
        _context.PageStore.SeedContent("spring-draft", """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" }, { "id": "s2", "type": "custom-stats" } ] }""");
        var unchanged = new GroupedSiteBuilderPage
        {
            Id = "about",
            StoreId = StoreA,
            Name = "About",
            Permalink = "/about",
            CultureName = "en-US",
            Pages = [new SiteBuilderPage { Id = "about-published", GroupId = "about", StoreId = StoreA, Status = "Published" }],
        };
        _context.PageStore.SeedGroup(unchanged);
        _context.PageStore.SeedContent("about-published", """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""");
        var neverPublished = _context.SeedDraft("landing", StoreA, """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""");
        _context.ListPages(() => [LandingRow(published), LandingRow(unchanged), LandingRow(neverPublished)]);

        var preview = await DryRunAsync();

        var placed = preview.Drafts.Single(x => x.Path == "default/custom-sections/custom-stats.json").PlacedSections!;
        Assert.Equal([("about", "published", 1), ("landing", "draft", 1), ("spring", "draft", 2), ("spring", "published", 1)],
            placed.Pages.Select(x => (x.PageId, x.Version, x.Sections)).Order());
        Assert.StartsWith("Scanned all 3 pages (3 language versions).", placed.Coverage, StringComparison.Ordinal);
        Assert.Contains("its published version and in its draft", placed.Coverage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDryRunAndTheApprovalPreview_WarnOfALiveCustomSectionTypeWhoseFileIsGone_WithThePagesThatPlaceIt()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        var first = await DryRunAsync();
        (await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = first.ExpectedVersions, expected_catalog_hash = first.CatalogHash },
            Approved)).EnsureSucceeded();
        _context.Blobs.Files.Remove(ThemeFileKey(StoreA, "custom-sections/custom-stats.json"));
        _context.SeedThemeFile(StoreA, "templates/product.bicycles.json", ProductTemplate, draft: true);
        var spring = _context.SeedPage("spring", StoreA, published: true, draft: false,
            content: """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""");
        var about = _context.SeedDraft("about", StoreA, """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""");
        _context.ListPages(() => [LandingRow(spring), LandingRow(about)]);

        var preview = await DryRunAsync();
        var held = await _context.CallAsync(Publisher(), Tool,
            new { store_id = StoreA, expected_versions = preview.ExpectedVersions, expected_catalog_hash = preview.CatalogHash });

        const string removed = "Section type 'Stats' (custom-stats) will be removed: it is live now, but its file is no longer in the theme, so this " +
            "publication leaves it out. 2 pages place it and will show nothing in its place: 'Page about' (/about) and 'Page spring' (/spring).";
        Assert.Contains(removed, preview.Warnings);
        Assert.Contains(preview.Changes, x => x.Field == "default/custom-sections/custom-stats.json" && x.From?.ToString() == "published" && x.To?.ToString() == "removed");
        Assert.Equal(["default/templates/product.bicycles.json"], preview.ExpectedVersions.Keys);
        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.Contains(removed, held.Read<ThemePublication>().Warnings);
    }

    [Fact]
    public async Task TheDryRunWithoutDrafts_DoesNotSayNothingChanges_WhenALiveCustomSectionTypeWouldBeRemoved()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        var first = await DryRunAsync();
        (await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = first.ExpectedVersions, expected_catalog_hash = first.CatalogHash },
            Approved)).EnsureSucceeded();
        _context.Blobs.Files.Remove(ThemeFileKey(StoreA, "custom-sections/custom-stats.json"));

        var preview = await DryRunAsync();

        Assert.Contains(preview.Warnings, x => x.StartsWith("Section type 'Stats' (custom-stats) will be removed", StringComparison.Ordinal) &&
            x.EndsWith("No page places it.", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Warnings, x => x.Contains("nothing customers see changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACorruptSnapshot_DoesNotFailTheDryRun()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        var first = await DryRunAsync();
        (await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = first.ExpectedVersions, expected_catalog_hash = first.CatalogHash },
            Approved)).EnsureSucceeded();
        var snapshot = Assert.Single(_context.Blobs.Files.Keys, x => x.StartsWith($"themes/{StoreA}/default/snapshots/", StringComparison.Ordinal) &&
            x.EndsWith("/en-US.json", StringComparison.Ordinal));
        _context.Blobs.Files[snapshot] = "{ not json";
        _context.Blobs.Files.Remove(ThemeFileKey(StoreA, "custom-sections/custom-stats.json"));

        var preview = await DryRunAsync();

        Assert.DoesNotContain(preview.Warnings, x => x.Contains("custom-stats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDryRun_DoesNotWarnOfALiveCustomSectionTypeThePublicationKeeps()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        var first = await DryRunAsync();
        (await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = first.ExpectedVersions, expected_catalog_hash = first.CatalogHash },
            Approved)).EnsureSucceeded();
        _context.SeedThemeFile(StoreA, "templates/product.bicycles.json", ProductTemplate, draft: true);

        var preview = await DryRunAsync();

        Assert.DoesNotContain(preview.Warnings, x => x.Contains("custom-stats", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Changes, x => x.To?.ToString() == "removed");
    }

    [Fact]
    public async Task TheDryRun_AnswersTheCatalogTheThemeIsCheckedAgainst()
    {
        var catalog = await SeedAsync();

        Assert.Equal(catalog, (await DryRunAsync()).CatalogHash);
    }

    [Fact]
    public async Task ACallWithTheVersionsButNotTheCatalog_IsRefused_AndPublishesNothing()
    {
        await SeedAsync();
        var preview = await DryRunAsync();

        var requesterCheck = await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = preview.ExpectedVersions, dryRun = true });
        var approved = await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = preview.ExpectedVersions }, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, requesterCheck.Outcome);
        Assert.Equal(McpToolCallOutcome.Failed, approved.Outcome);
        Assert.Contains("expected_catalog_hash", approved.Error, StringComparison.Ordinal);
        Assert.Null(_context.ReadThemeFile(StoreA, "templates/product.bicycles.json"));
    }

    [Fact]
    public async Task ACatalogUploadedAfterTheReview_RefusesTheApprovedPublication()
    {
        await SeedAsync();
        var preview = await DryRunAsync();
        var moved = new JObject { ["hash"] = "moved-catalog", ["sections"] = new JObject(ThemeSections.Select(x => new JProperty(x, new JObject()))) };
        await new SchemaCatalogService(_context.ThemeStorage).SaveAsync(StoreA, moved.ToString(), TestContext.Current.CancellationToken);

        var approved = await _context.CallAsync(Publisher(), Tool,
            new { store_id = StoreA, expected_versions = preview.ExpectedVersions, expected_catalog_hash = preview.CatalogHash }, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, approved.Outcome);
        Assert.Contains("schema catalog changed", approved.Error, StringComparison.Ordinal);
        Assert.Null(_context.ReadThemeFile(StoreA, "templates/product.bicycles.json"));
    }

    [Fact]
    public async Task ACallWithTheVersions_WaitsForApproval_AndPublishesNothing()
    {
        await SeedAsync();
        var preview = await DryRunAsync();

        var result = await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = preview.ExpectedVersions, expected_catalog_hash = preview.CatalogHash });

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

        var result = (await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = preview.ExpectedVersions, expected_catalog_hash = preview.CatalogHash }, Approved))
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

        var requesterCheck = await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = preview.ExpectedVersions, expected_catalog_hash = preview.CatalogHash, dryRun = true });
        var approved = await _context.CallAsync(Publisher(), Tool, new { store_id = StoreA, expected_versions = preview.ExpectedVersions, expected_catalog_hash = preview.CatalogHash }, Approved);

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
