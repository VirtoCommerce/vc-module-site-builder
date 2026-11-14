using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class SearchSitePagesSectionFilterTests : IAsyncDisposable
{
    private const string Tool = "search_site_pages";
    private const string WithHero = """{ "settings": {}, "content": [ { "id": "t1", "type": "text" }, { "id": "h1", "type": "hero-banner", "title": "Black Friday" } ] }""";
    private const string WithoutHero = """{ "settings": {}, "content": [ { "id": "t1", "type": "text" } ] }""";

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;
    private readonly List<string> _ids = [];

    public SearchSitePagesSectionFilterTests()
    {
        _pages = _context.UsePageStore();
        _context.ListPages(() => _pages.GetAsync(_ids).GetAwaiter().GetResult().Select(LandingRow));

        Seed("a1", StoreA, WithHero);
        Seed("a2", StoreA, WithoutHero);
        Seed("a3", StoreA, WithHero.Replace("Black Friday", "Cyber Monday"));
        Seed("b1", StoreB, WithHero);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public async Task ASectionType_ListsThePagesThatUseIt_WithTheirSections()
    {
        var search = await SearchAsync(Reader(), new { section_type = "hero-banner" });

        Assert.Equal(["a1", "a3", "b1"], search.Items.Select(x => x.Id));
        var match = Assert.Single(search.Items[0].Matches);
        Assert.Equal(("a1", "h1", 1), (match.PageId, Assert.Single(match.Sections).Id, match.Sections[0].Position));
        Assert.Equal(await TokenAsync("a1"), match.VersionToken);
        Assert.True(search.IsComplete);
        Assert.Equal((4, 4), (search.TotalCount, search.ScannedCount));
        Assert.StartsWith("Scanned all 4 pages", search.Coverage);
    }

    [Fact]
    public async Task ASettingsCondition_NarrowsTheSections()
    {
        var search = await SearchAsync(Reader(), new { section_type = "hero-banner", setting_path = "title", setting_contains = "friday" });

        Assert.Equal(["a1", "b1"], search.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task AScanOfSomePages_SaysWhereToContinue_AndNeverThatThereIsNone()
    {
        var first = await SearchAsync(Reader(), new { section_type = "hero-banner", take = 2 });
        var second = await SearchAsync(Reader(), new { section_type = "hero-banner", skip = first.NextSkip, take = 2 });

        Assert.Equal(["a1"], first.Items.Select(x => x.Id));
        Assert.Equal((2, false, (int?)2), (first.ScannedCount, first.IsComplete, first.NextSkip));
        Assert.Contains("were not scanned", first.Coverage);
        Assert.Contains("skip=2", first.Coverage);
        Assert.Equal(["a3", "b1"], second.Items.Select(x => x.Id));
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task AStoreReader_ScansOnlyTheirStore()
    {
        var search = await SearchAsync(StoreAReader(), new { section_type = "hero-banner" });

        Assert.Equal(["a1", "a3"], search.Items.Select(x => x.Id));
        Assert.Equal(3, search.TotalCount);
    }

    [Fact]
    public async Task AStoreReader_IsRefusedAnotherStore()
    {
        var result = await _context.CallAsync(StoreAReader(), Tool, new { store_id = StoreB, section_type = "hero-banner" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task ASettingsCondition_WithoutASectionType_IsRefused()
    {
        var result = await _context.CallAsync(Reader(), Tool, new { setting_path = "title", setting_equals = "x" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("section_type", result.Error);
    }

    [Fact]
    public async Task ASharedComponentsSections_AreReadOnlyWithSharedComponentsRead()
    {
        _context.SharedComponents
            .Setup(x => x.GetAsync(It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync([new SiteBuilderSharedComponent { Id = "sc1", StoreId = StoreA }]);
        _context.SharedComponentContents
            .Setup(x => x.TryLoadContentAsync(It.IsAny<SiteBuilderSharedComponent>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync("""{ "settings": {}, "content": [ { "id": "c1", "type": "faq" } ] }""");
        Seed("a4", StoreA, """{ "settings": {}, "content": [ { "id": "pl1", "type": "componentRef", "componentRef": "sc1" } ] }""");

        var withoutRead = await SearchAsync(Reader(), new { store_id = StoreA, section_type = "faq" });
        var withRead = await SearchAsync(McpTestUser.Create("components").WithPermissions(Permissions.Read, Permissions.SharedComponentsRead),
            new { store_id = StoreA, section_type = "faq" });

        Assert.Equal(["sc1"], Assert.Single(Assert.Single(withoutRead.Items).Matches).UnreadSharedComponentIds);
        Assert.Contains("builder:shared-components:read", withoutRead.Coverage);
        var section = Assert.Single(Assert.Single(Assert.Single(withRead.Items).Matches).Sections);
        Assert.Equal(("c1", "sc1", "pl1"), (section.Id, section.SharedComponentId, section.PlacementId));
    }

    [Fact]
    public async Task ThePublishedVersion_CanBeScanned()
    {
        var search = await SearchAsync(Reader(), new { section_type = "hero-banner", version = "published" });

        Assert.Empty(search.Items);
        Assert.Equal(4, search.Unread.Count);
        Assert.All(search.Unread, x => Assert.Equal("has no published version", x.Reason));
    }

    private async Task<SitePageSectionSearch> SearchAsync(McpTestUser user, object arguments)
    {
        var result = await _context.CallAsync(user, Tool, arguments);
        return result.EnsureSucceeded().Read<SitePageSectionSearch>();
    }

    private async Task<string> TokenAsync(string id)
    {
        var group = (await _pages.GetAsync([id])).Single();
        return (await _pages.LoadGroupContentAsync(group, cancellationToken: TestContext.Current.CancellationToken)).ETag;
    }

    private void Seed(string id, string storeId, string content)
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
        _ids.Add(id);
    }
}
