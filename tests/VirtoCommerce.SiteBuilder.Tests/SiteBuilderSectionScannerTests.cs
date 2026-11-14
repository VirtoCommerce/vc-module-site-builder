using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using Xunit;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Tests;

public sealed class SiteBuilderSectionScannerTests
{
    private const string Hero = "hero-banner";

    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages = new();
    private readonly Mock<ISiteBuilderPageListService> _list = new();
    private readonly Mock<ISiteBuilderSharedComponentService> _components = new();
    private readonly Mock<ISiteBuilderSharedComponentContentService> _componentContents = new();
    private readonly FakeBlobStorage _blobs = new();
    private readonly List<SiteBuilderPageListItem> _rows = [];
    private readonly Dictionary<string, string> _componentContent = new();
    private readonly List<SiteBuilderSharedComponent> _componentList = [];

    public SiteBuilderSectionScannerTests()
    {
        _list
            .Setup(x => x.SearchAsync(It.IsAny<SiteBuilderPageSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteBuilderPageSearchCriteria criteria, CancellationToken _) =>
            {
                var matches = _rows.Where(x => criteria.StoreIds == null || criteria.StoreIds.Contains(x.Page?.StoreId ?? x.MultilanguagePage?.StoreId)).ToList();
                return new SiteBuilderPageListSearchResult { TotalCount = matches.Count, Results = matches.Skip(criteria.Skip).Take(criteria.Take).ToList() };
            });

        _components
            .Setup(x => x.GetAsync(It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IList<string> ids, string _, bool _) => _componentList.Where(x => ids.Contains(x.Id)).ToList());
        _componentContents
            .Setup(x => x.TryLoadContentAsync(It.IsAny<SiteBuilderSharedComponent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteBuilderSharedComponent component, CancellationToken _) => _componentContent.GetValueOrDefault(component.Id));
    }

    private SiteBuilderSectionScanner Scanner => new(_list.Object, _pages, _components.Object, _componentContents.Object,
        new ThemeStorageFactory(_blobs.StoreService, _blobs, _blobs, _blobs));

    [Fact]
    public async Task TheCurrentShape_CountsSectionsAtSectionPositionsOnly()
    {
        Landing("p1", """
            { "settings": { "hero": { "type": "hero-banner" } }, "content": [
              { "id": "h1", "type": "hero-banner" },
              { "id": "t1", "type": "text", "nested": { "type": "hero-banner" }, "blocks": [ { "id": "b1", "type": "hero-banner" } ] },
              { "id": "h2", "type": "hero-banner", "hidden": true }
            ] }
            """);

        var page = Assert.Single((await ScanAsync()).Pages);

        Assert.Equal(["h1", "h2"], page.Sections.Select(x => x.SectionId));
        Assert.Equal([0, 2], page.Sections.Select(x => x.Position));
        Assert.True(page.Sections[1].Hidden);
        Assert.False(page.IsLegacyShape);
    }

    [Fact]
    public async Task TheOlderShape_IsScanned_WithoutItsSettings()
    {
        Landing("p1", """[ { "type": "hero-banner" }, { "id": "h1", "type": "hero-banner" }, { "id": "t1", "type": "text" } ]""");

        var page = Assert.Single((await ScanAsync()).Pages);

        Assert.True(page.IsLegacyShape);
        Assert.Equal(["h1"], page.Sections.Select(x => x.SectionId));
    }

    [Fact]
    public async Task ASectionWithoutAnId_GetsTheIdTheToolsGiveIt()
    {
        const string content = """{ "settings": {}, "content": [ { "type": "text" }, { "type": "hero-banner" } ] }""";
        Landing("p1", content);

        var match = Assert.Single(Assert.Single((await ScanAsync()).Pages).Sections);

        Assert.Equal(SiteBuilderPageItems.GetId(SiteBuilderPageDocument.Read(content)!.Sections[1]), match.SectionId);
    }

    [Fact]
    public async Task TheSectionsOfTheThemesHeaderAndFooter_AreNotThePages()
    {
        Landing("p1", """{ "settings": {}, "content": [ { "id": "hd", "type": "hero-banner", "__group": "header" }, { "id": "h1", "type": "hero-banner" } ] }""");

        Assert.Equal(["h1"], Assert.Single((await ScanAsync()).Pages).Sections.Select(x => x.SectionId));
    }

    [Fact]
    public async Task TheVersion_IsChosenBeforeTheFilter()
    {
        Landing("p1", published: """{ "settings": {}, "content": [ { "id": "old", "type": "hero-banner" } ] }""",
            draft: """{ "settings": {}, "content": [ { "id": "new", "type": "text" } ] }""");

        var draft = await ScanAsync();
        var published = await ScanAsync(x => x.Published = true);

        Assert.Empty(draft.Pages);
        Assert.Equal("old", Assert.Single(Assert.Single(published.Pages).Sections).SectionId);
        Assert.Equal("p1-published", Assert.Single(published.Pages).ContentPageId);
    }

    [Fact]
    public async Task APageNeverPublished_HasNoPublishedVersionToScan()
    {
        Landing("p1", """{ "settings": {}, "content": [ { "id": "h1", "type": "hero-banner" } ] }""");

        var scan = await ScanAsync(x => x.Published = true);

        Assert.Empty(scan.Pages);
        Assert.Equal(SiteBuilderSectionScanSkip.NotPublished, Assert.Single(scan.Unread).Reason);
    }

    [Fact]
    public async Task ASectionOfAPlacedSharedComponent_IsReportedWithItsComponent()
    {
        Component("sc1", "s1", """{ "settings": {}, "content": [ { "id": "c1", "type": "hero-banner" }, { "id": "c2", "type": "text" } ] }""");
        Landing("p1", """{ "settings": {}, "content": [ { "id": "t1", "type": "text" }, { "id": "pl1", "type": "componentRef", "componentRef": "sc1" } ] }""");

        var match = Assert.Single(Assert.Single((await ScanAsync()).Pages).Sections);

        Assert.Equal("c1", match.SectionId);
        Assert.Equal("sc1", match.SharedComponentId);
        Assert.Equal("pl1", match.PlacementId);
        Assert.Equal(1, match.Position);
    }

    [Fact]
    public async Task ASharedComponentOfAStoreNotOpened_IsReportedUnread()
    {
        Component("sc1", "s1", """{ "settings": {}, "content": [ { "id": "c1", "type": "hero-banner" } ] }""");
        Landing("p1", """{ "settings": {}, "content": [ { "id": "pl1", "type": "componentRef", "componentRef": "sc1" } ] }""");

        var page = Assert.Single((await ScanAsync(x => x.SharedComponentStoreIds = [])).Pages);

        Assert.Empty(page.Sections);
        Assert.Equal(["sc1"], page.UnreadSharedComponentIds);
    }

    [Fact]
    public async Task ASharedComponentOfAnotherStore_IsNotRead()
    {
        Component("sc1", "other-store", """{ "settings": {}, "content": [ { "id": "c1", "type": "hero-banner" } ] }""");
        Landing("p1", """{ "settings": {}, "content": [ { "id": "pl1", "type": "componentRef", "componentRef": "sc1" } ] }""");

        Assert.Equal(["sc1"], Assert.Single((await ScanAsync()).Pages).UnreadSharedComponentIds);
    }

    [Theory]
    [InlineData("title", "equals", "Spring sale", null, true)]
    [InlineData("title", "equals", "spring sale", null, false)]
    [InlineData("title", "contains", "SPRING", null, true)]
    [InlineData("button.url", "contains", "/black-friday", null, true)]
    [InlineData("button.url", "contains", "/cyber", null, false)]
    [InlineData("blocks.b2.text", "equals", "Second", null, true)]
    [InlineData("blocks.1.text", "equals", "Second", null, true)]
    [InlineData("blocks.*.text", "contains", "seco", null, true)]
    [InlineData("blocks.b1.text", "contains", "seco", null, false)]
    [InlineData("count", "equals", "3", null, true)]
    [InlineData("subtitle", "contains", "soldes", null, true)]
    [InlineData("subtitle", "contains", "soldes", "fr-FR", true)]
    [InlineData("subtitle", "contains", "soldes", "en-US", false)]
    [InlineData("subtitle", "equals", "Sale", "EN-us", true)]
    [InlineData("missing", "equals", "x", null, false)]
    public async Task ASettingsCondition_SelectsTheSections(string path, string op, string value, string language, bool matches)
    {
        Landing("p1", """
            { "settings": {}, "content": [ {
              "id": "h1", "type": "hero-banner", "title": "Spring sale", "count": 3,
              "button": { "url": "https://shop/black-friday" },
              "subtitle": { "en-US": "Sale", "fr-FR": "Soldes" },
              "blocks": [ { "id": "b1", "type": "item", "text": "First" }, { "id": "b2", "type": "item", "text": "Second" } ]
            } ] }
            """);

        var scan = await ScanAsync(x => x.Condition = new SiteBuilderSectionSettingCondition { Path = path, Operator = op, Value = value, Language = language });

        Assert.Equal(matches, scan.Pages.Count == 1);
    }

    [Fact]
    public async Task AnInvalidCondition_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ScanAsync(x => x.Condition = new SiteBuilderSectionSettingCondition { Path = "a..b", Value = "x" }));
        await Assert.ThrowsAsync<ArgumentException>(() => ScanAsync(x => x.Condition = new SiteBuilderSectionSettingCondition { Path = "a", Operator = "like", Value = "x" }));
    }

    [Fact]
    public async Task ALimitedScan_SaysHowMuchItCovered()
    {
        for (var i = 1; i <= 5; i++)
        {
            Landing($"p{i}", """{ "settings": {}, "content": [ { "id": "h1", "type": "hero-banner" } ] }""");
        }

        var first = await ScanAsync(x => x.MaxPages = 2);
        var last = await ScanAsync(x => { x.MaxPages = 2; x.Pages.Skip = 4; });
        var all = await ScanAsync(x => x.MaxPages = 10);

        Assert.Equal((5, 2, 2, false, (int?)2), (first.TotalPageCount, first.ScannedPageCount, first.Pages.Count, first.IsComplete, first.NextSkip));
        Assert.Equal((1, false, (int?)null), (last.ScannedPageCount, last.IsComplete, last.NextSkip));
        Assert.Equal((5, true, (int?)null), (all.ScannedPageCount, all.IsComplete, all.NextSkip));
    }

    [Fact]
    public async Task ThePagesOfAMultilanguagePage_AreScannedOneByOne_InTheLanguagesAndStatusesAsked()
    {
        var en = Seed("en", "s1", "en-US", Published, """{ "settings": {}, "content": [ { "id": "h1", "type": "hero-banner" } ] }""");
        var fr = Seed("fr", "s1", "fr-FR", Draft, """{ "settings": {}, "content": [ { "id": "h1", "type": "hero-banner" } ] }""");
        _rows.Add(new SiteBuilderPageListItem
        {
            Type = "Multilanguage",
            Id = "ml1",
            MultilanguagePage = new MultilanguagePage { Id = "ml1", StoreId = "s1" },
            Languages =
            [
                new MultilanguagePageLanguage { CultureName = "en-US", Page = en, IsDefault = true },
                new MultilanguagePageLanguage { CultureName = "fr-FR", Page = fr },
                new MultilanguagePageLanguage { CultureName = "de-DE", Status = MultilanguagePageLanguage.Missing },
            ],
        });

        var all = await ScanAsync();
        var french = await ScanAsync(x => x.Pages.LanguageCode = "FR-fr");
        var published = await ScanAsync(x => x.Pages.Statuses = Published);

        Assert.Equal((2, 1), (all.ScannedVersionCount, all.ScannedPageCount));
        Assert.Equal(["en", "fr"], all.Pages.Select(x => x.Page.Id));
        Assert.All(all.Pages, x => Assert.Equal("ml1", x.ListItem.Id));
        Assert.Equal(["fr"], french.Pages.Select(x => x.Page.Id));
        Assert.Equal(["en"], published.Pages.Select(x => x.Page.Id));
    }

    [Fact]
    public async Task AVersionThatCannotBeRead_IsReportedUnread()
    {
        Landing("p1", "not json");
        _rows.Add(LandingRow(new GroupedSiteBuilderPage { Id = "gone", StoreId = "s1", Pages = [new SiteBuilderPage { Id = "gone-draft", Status = Draft }] }));

        var scan = await ScanAsync();

        Assert.Equal(2, scan.ScannedVersionCount);
        Assert.Equal([SiteBuilderSectionScanSkip.NotADocument, SiteBuilderSectionScanSkip.Deleted], scan.Unread.Select(x => x.Reason));
    }

    [Fact]
    public async Task TheStoresOfTheFilter_AreTheOnlyOnesScanned()
    {
        Landing("p1", """{ "settings": {}, "content": [ { "id": "h1", "type": "hero-banner" } ] }""");
        _rows.Add(LandingRow(Seed("p2", "s2", "en-US", Draft, """{ "settings": {}, "content": [ { "id": "h1", "type": "hero-banner" } ] }""")));

        var scan = await ScanAsync(x => x.Pages.StoreIds = ["s2"]);

        Assert.Equal(["p2"], scan.Pages.Select(x => x.Page.Id));
    }

    [Fact]
    public async Task Templates_AreScannedInTheVersionAsked()
    {
        var storage = await new ThemeStorageFactory(_blobs.StoreService, _blobs, _blobs, _blobs).OpenAsync("s1");
        await storage.WriteAsync("templates/product.json", """{ "settings": {}, "content": [ { "id": "h1", "type": "hero-banner" } ] }""");
        await storage.WriteAsync(storage.GetDraftPath("templates/product.json"), """{ "settings": {}, "content": [ { "id": "t1", "type": "text" } ] }""");
        await storage.WriteAsync(storage.GetDraftPath("templates/page.json"), """{ "settings": {}, "content": [ { "id": "h2", "type": "hero-banner" } ] }""");
        await storage.WriteAsync("templates/category.json", "not json");

        var published = await Scanner.ScanTemplatesAsync(new SiteBuilderTemplateSectionScanCriteria { StoreId = "s1", Published = true, SectionType = Hero },
            TestContext.Current.CancellationToken);
        var draft = await Scanner.ScanTemplatesAsync(new SiteBuilderTemplateSectionScanCriteria { StoreId = "s1", SectionType = Hero },
            TestContext.Current.CancellationToken);

        Assert.Equal((2, "product"), (published.TemplateCount, Assert.Single(published.Templates).Key));
        Assert.Equal(["category"], published.UnreadTemplateKeys);
        Assert.Equal(3, draft.TemplateCount);
        var page = Assert.Single(draft.Templates);
        Assert.Equal(("page", true, "h2"), (page.Key, page.IsDraft, Assert.Single(page.Sections).SectionId));
    }

    private Task<SiteBuilderSectionScanResult> ScanAsync(Action<SiteBuilderSectionScanCriteria> configure = null)
    {
        var criteria = new SiteBuilderSectionScanCriteria { MaxPages = 50, SectionType = Hero };
        configure?.Invoke(criteria);
        return Scanner.ScanPagesAsync(criteria, TestContext.Current.CancellationToken);
    }

    private void Landing(string id, string draft = null, string published = null)
    {
        var pages = new List<SiteBuilderPage>();
        if (published != null)
        {
            pages.Add(new SiteBuilderPage { Id = $"{id}-published", GroupId = id, StoreId = "s1", Status = Published });
            _pages.SeedContent($"{id}-published", published);
        }

        if (draft != null)
        {
            pages.Add(new SiteBuilderPage { Id = $"{id}-draft", GroupId = id, StoreId = "s1", Status = Draft });
            _pages.SeedContent($"{id}-draft", draft);
        }

        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = id, StoreId = "s1", Name = id, CultureName = "en-US", Pages = pages });
        _rows.Add(LandingRow(_pages.GetAsync([id]).GetAwaiter().GetResult().Single()));
    }

    private GroupedSiteBuilderPage Seed(string id, string storeId, string language, string status, string content)
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = id,
            StoreId = storeId,
            Name = id,
            CultureName = language,
            Pages = [new SiteBuilderPage { Id = $"{id}-version", GroupId = id, StoreId = storeId, Status = status }],
        });
        _pages.SeedContent($"{id}-version", content);

        return _pages.GetAsync([id]).GetAwaiter().GetResult().Single();
    }

    private void Component(string id, string storeId, string content)
    {
        _componentList.Add(new SiteBuilderSharedComponent { Id = id, StoreId = storeId, Name = id });
        _componentContent[id] = content;
    }

    private static SiteBuilderPageListItem LandingRow(GroupedSiteBuilderPage page) => new() { Type = "Landing", Id = page.Id, Name = page.Name, Page = page };
}
