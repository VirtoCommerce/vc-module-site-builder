using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;
using Xunit;
using Fakes = VirtoCommerce.SiteBuilder.Tests.PublishedRenameContentPreservationTests;

namespace VirtoCommerce.SiteBuilder.Tests;

public class CustomSectionSaveTests
{
    private const string Catalog = "abc12345";
    private const string Hero = "default/custom-sections/custom-hero.json";
    private const string Product = "default/templates/product.json";
    private const string Published = """{"settings":{},"content":[]}""";
    private const string HeroFile = """
        {
          "format": 1,
          "name": "Hero",
          "schema": { "settings": [ { "id": "title", "type": "string", "translatable": true }, { "id": "body", "type": "text" } ] },
          "template": "<section><h2>{{ settings.title }}</h2><div data-html=\"settings.body\"></div></section>",
          "css": ":scope { color: var(--color-neutral-900); }"
        }
        """;

    private const string Kit = """, "kit": { "tags": { "vc-button": { "attributes": { "label": { "type": "string" } } } }, "behaviors": {} }""";

    private readonly FakeBlobStorage _live = new();
    private readonly Fakes.FakeGroupedPageService _pages = new();
    private readonly RecordingContentHistory _history = new();
    private readonly InProcessDistributedLock _lock = new();
    private readonly ThemeStorageFactory _themes;
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

    public CustomSectionSaveTests()
    {
        _themes = new ThemeStorageFactory(_live.StoreService, _live, _live, _live);
        _pages.SeedGroup(new GroupedSiteBuilderPage { Id = "g1", StoreId = "s1", Pages = [new SiteBuilderPage { Id = "p1", StoreId = "s1", Status = ModuleConstants.PageStatuses.Published }] });
        _pages.SeedContent("p1", Published);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SaveAsync_CustomSectionType_IsSavedAsADraftWithItsWarnings()
    {
        await SaveCatalogAsync();
        var hero = HeroWith(template: "<section><h2>Sale</h2><p>{{ settings.title }}</p></section>");

        var result = await Operations().SaveAsync(SaveRequest(Save(null, (Hero, hero))), _user, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(hero, _live.Files[Theme($"{Hero}-draft")]);
        var file = Assert.Single(result.Value.ThemeFiles);
        Assert.Equal(Hero, file.Path);
        Assert.Equal(["the template holds text of its own ('Sale'), which is the same in every language: bind it to a translatable setting"], file.Warnings);
    }

    [Fact]
    public async Task SaveAsync_CustomSectionType_IsCheckedOnceBeforeTheWrite()
    {
        await SaveCatalogAsync();
        var validator = new CountingValidator(TestServices.CustomSectionValidator());

        var result = await Operations(validator).SaveAsync(SaveRequest(Save(null, (Hero, HeroWith(template: "<section><h2>Sale</h2></section>")))), _user, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(1, validator.Checked);
        Assert.NotEmpty(Assert.Single(result.Value.ThemeFiles).Warnings);
    }

    [Fact]
    public async Task SaveAsync_DryRunOfACustomSectionType_AnswersItsWarningsAndWritesNothing()
    {
        await SaveCatalogAsync();
        var request = SaveRequest(Save(null, (Hero, HeroWith(css: ":scope { color: #000; }"))));
        request.DryRun = true;

        var result = await Operations().SaveAsync(request, _user, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["custom-sections/custom-hero.json: the CSS sets literal colors (color), which the palette does not change: use var(--color-…) or a color-token setting's variable"],
            result.Preview.Warnings);
        Assert.DoesNotContain(_live.Files.Keys, x => x.EndsWith("-draft"));
    }

    [Fact]
    public async Task SaveAsync_DryRunOfAMaskFromAGradient_AnswersWithNoWarning()
    {
        await SaveCatalogAsync();
        var request = SaveRequest(Save(null, (Hero, HeroWith(css: ":scope { mask-image: linear-gradient(to bottom, black 60%, transparent); }"))));
        request.DryRun = true;

        var result = await Operations().SaveAsync(request, _user, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(result.Preview.Warnings);
    }

    [Fact]
    public async Task SaveAsync_CustomSectionTypeThatBreaksARule_IsInvalidAndWritesNothing()
    {
        await SaveCatalogAsync();

        var result = await Operations().SaveAsync(SaveRequest(Save(null, (Hero, HeroWith(template: "<section onclick=\"go()\"></section>")))), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Equal(["custom-sections/custom-hero.json: <section> at line 1 has an attribute a custom section cannot use, 'onclick': it runs script"], result.Errors);
        Assert.DoesNotContain(_live.Files.Keys, x => x.EndsWith("-draft"));
    }

    [Fact]
    public async Task SaveAsync_KitTag_IsCheckedAgainstTheKitOfTheCatalogTheSaveNames()
    {
        await SaveCatalogAsync(kit: Kit);
        const string button = "<section><vc-button label=\"{{ settings.title }}\"></vc-button></section>";
        const string badge = "<section><vc-badge label=\"{{ settings.title }}\"></vc-badge></section>";

        var saved = await Operations().SaveAsync(SaveRequest(Save(null, (Hero, HeroWith(template: button)))), _user, Ct);
        var refused = await Operations().SaveAsync(SaveRequest(Save(null, ("default/custom-sections/custom-badge.json", HeroWith(template: badge)))), _user, Ct);

        Assert.True(saved.Succeeded, saved.Error);
        Assert.Equal(["custom-sections/custom-badge.json: <vc-badge> at line 1 is not a kit tag the storefront offers"], refused.Errors);
    }

    [Fact]
    public async Task SaveAsync_ThemeHoldingATypeThatBreaksARule_SavesTheFilesThatDoNotWriteIt()
    {
        await SaveCatalogAsync(""", "site-header": {}""");
        _live.Files[Theme($"{Hero}-draft")] = HeroWith(template: "<section onclick=\"go()\"></section>");
        const string headerFile = """{ "content": [ { "id": "h", "type": "site-header" } ] }""";

        var header = await Operations().SaveAsync(SaveRequest(Save(null, ("default/sections/header-group.json", headerFile))), _user, Ct);
        var template = await Operations().SaveAsync(SaveRequest(Save(null, (Product, TemplateOf("product-info", "custom-hero")))), _user, Ct);

        Assert.True(header.Succeeded, header.Error);
        Assert.True(template.Succeeded, template.Error);
    }

    [Fact]
    public async Task DiscardAsync_DraftOfATypeNeverPublished_RemovesTheType()
    {
        _live.Files[Theme($"{Hero}-draft")] = HeroFile;
        var request = new DesignerDiscardOperationRequest { StoreId = "s1", SaveId = "save-0001", Changes = new DesignerDiscardRequest { ThemeFiles = [Hero] } };
        request.DryRun = true;
        var preview = await Operations().DiscardAsync(request, _user, Ct);
        request.DryRun = false;

        var result = await Operations().DiscardAsync(request, _user, Ct);

        Assert.Empty(preview.Preview.Warnings);
        Assert.Equal((ModuleConstants.ChangePreview.ThemeFile, Hero), (preview.Preview.Objects.Single().ObjectType, preview.Preview.Objects.Single().ObjectId));
        Assert.True(result.Succeeded, result.Error);
        Assert.DoesNotContain(_live.Files.Keys, x => x.Contains("custom-sections/", System.StringComparison.Ordinal));
        Assert.Empty(await new CustomSectionService(_themes, new ThemeDraftService(_themes, _lock, _live, NullLogger<ThemeDraftService>.Instance)).ListTypesAsync("s1", null, Ct));
    }

    [Fact]
    public async Task ValidateThemeFilesAsync_WithoutACatalogHash_RefusesAHeaderThatPlacesACustomType()
    {
        var designer = TestServices.Designer(_history, themeDrafts: new ThemeDraftService(_themes, _lock, _live, NullLogger<ThemeDraftService>.Instance), pages: _pages,
            themes: _themes);
        var header = JObject.Parse("""{ "type": "header", "content": [ { "id": "promo", "type": "custom-hero" } ] }""");

        var exception = await Assert.ThrowsAsync<ThemeValidationException>(() =>
            designer.ValidateThemeFilesAsync("s1", new Dictionary<string, JToken> { ["default/sections/header-group.json"] = header }, draft: true, catalogHash: null, Ct));

        Assert.Equal(["sections/header-group.json: 'promo' places a custom section, which only a page or a template can hold"], exception.Errors);
    }

    [Fact]
    public async Task RestoreAsync_TypeOfASecondThemeThatBreaksARule_IsRefused()
    {
        var restore = Designer().RestoreAsync(new ContentRestoreCommand
        {
            StoreId = "s1",
            SaveId = "restore-0001",
            VersionId = "1234567",
            Contents = new Dictionary<HistoryItem, string>
            {
                [new HistoryItem(HistoryItemType.Theme, "default/templates/product.json")] = """{ "settings": {}, "content": [] }""",
                [new HistoryItem(HistoryItemType.Theme, "other/custom-sections/custom-hero.json")] = HeroWith(template: "<section><script>x()</script></section>"),
            },
        }, _user, Ct);

        var exception = await Assert.ThrowsAsync<ThemeValidationException>(() => restore);

        Assert.Contains("custom-sections/custom-hero.json: <script> at line 1 is not an element a custom section can hold", exception.Errors);
    }

    [Fact]
    public async Task PreviewSaveAsync_PagePlacingATypeOfTheStoresTheme_IsSanitizedWhateverThemeItsFilesAreIn()
    {
        await SaveCatalogAsync();
        _live.Files[Theme($"{Hero}-draft")] = HeroFile;
        var command = new DesignerSaveCommand
        {
            StoreId = "s1",
            SaveId = "save-0001",
            Group = await _pages.GetByIdAsync("g1"),
            PageContent = """{"settings":{},"content":[{"id":"a","type":"custom-hero","body":"<p>Hi</p><img src=x onerror=alert(1)>"}]}""",
            ThemeFiles = [new DesignerSaveFile { Path = "other/templates/page.json", Content = """{"settings":{},"content":[]}""" }],
            CatalogHash = Catalog,
        };

        await Designer().PreviewSaveAsync(command, _user, Ct);

        Assert.Equal("<p>Hi</p><img src=\"x\">", JObject.Parse(command.PageContent)["content"]![0]!.Value<string>("body"));
    }

    [Fact]
    public async Task RestoreAsync_TypeThatBreaksARule_IsRefusedAndWritesNothing()
    {
        var restore = TestServices.Designer(_history, themeDrafts: new ThemeDraftService(_themes, _lock, _live, NullLogger<ThemeDraftService>.Instance), pages: _pages,
                themes: _themes)
            .RestoreAsync(new ContentRestoreCommand
            {
                StoreId = "s1",
                SaveId = "restore-0001",
                VersionId = "1234567",
                Contents = new Dictionary<HistoryItem, string> { [new HistoryItem(HistoryItemType.Theme, Hero)] = HeroWith(template: "<section><script>x()</script></section>") },
            }, _user, Ct);

        var exception = await Assert.ThrowsAsync<ThemeValidationException>(() => restore);

        Assert.Contains("custom-sections/custom-hero.json: <script> at line 1 is not an element a custom section can hold", exception.Errors);
        Assert.DoesNotContain(_live.Files.Keys, x => x.EndsWith("-draft", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveAsync_FileOfTheFolderNamedAsNoType_IsInvalid()
    {
        await SaveCatalogAsync();

        var result = await Operations().SaveAsync(SaveRequest(Save(null, ("default/custom-sections/Hero.json", HeroFile))), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Equal(["custom-sections/Hero.json: 'Hero' is not a custom section type, which matches ^custom-[a-z0-9]+(?:-[a-z0-9]+)*$ and has at most 64 characters"],
            result.Errors);
    }

    [Fact]
    public async Task SaveAsync_TemplatePlacingACustomTypeOfTheDrafts_IsSaved()
    {
        await SaveCatalogAsync();
        _live.Files[Theme($"{Hero}-draft")] = HeroFile;

        var result = await Operations().SaveAsync(SaveRequest(Save(null, (Product, TemplateOf("product-info", "custom-hero")))), _user, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(_live.Files.ContainsKey(Theme($"{Product}-draft")));
    }

    [Fact]
    public async Task SaveAsync_TypeAndATemplateThatPlacesIt_AreSavedTogether()
    {
        await SaveCatalogAsync();

        var result = await Operations().SaveAsync(SaveRequest(Save(null, (Hero, HeroFile), (Product, TemplateOf("product-info", "custom-hero")))), _user, Ct);

        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task SaveAsync_HeaderGroupPlacingACustomType_IsInvalidAndWritesNothing()
    {
        await SaveCatalogAsync(""", "site-header": {}""");
        _live.Files[Theme($"{Hero}-draft")] = HeroFile;
        const string header = """{ "content": [ { "id": "h", "type": "site-header" }, { "id": "promo", "type": "custom-hero" } ] }""";

        var result = await Operations().SaveAsync(SaveRequest(Save(null, ("default/sections/header-group.json", header))), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Equal(["sections/header-group.json: 'promo' places a custom section, which only a page or a template can hold"], result.Errors);
        Assert.False(_live.Files.ContainsKey(Theme("default/sections/header-group.json-draft")));
    }

    [Fact]
    public async Task SaveAsync_TemplatePlacingACustomTypeOnlyThePreviewSaw_IsInvalid()
    {
        await SaveCatalogAsync(""", "custom-old": { "settings": [], "custom": true }""");

        var result = await Operations().SaveAsync(SaveRequest(Save(null, (Product, TemplateOf("product-info", "custom-old")))), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Equal(["templates/product.json: 's1' has a type the storefront does not have, 'custom-old'"], result.Errors);
    }

    [Fact]
    public async Task SaveAsync_TemplateWithASectionList_TakesACustomTypeUnlessItNamesOtherTemplates()
    {
        await new SchemaCatalogService(_themes).SaveAsync("s1",
            $$"""
            {
              "hash": "{{Catalog}}",
              "templates": { "product": { "type": "themes", "previewMessage": { "templateKey": "product" }, "sections": [ "product-info" ] } },
              "sections": { "product-info": {} },
              "blocks": {},
              "shared": {}
            }
            """, Ct);
        var listing = JObject.Parse(HeroFile);
        listing["schema"]!["targetTemplates"] = new JArray("category");
        _live.Files[Theme($"{Hero}-draft")] = HeroFile;
        _live.Files[Theme("default/custom-sections/custom-listing.json-draft")] = listing.ToString();

        var result = await Operations().SaveAsync(SaveRequest(Save(null, (Product, TemplateOf("product-info", "custom-hero", "custom-listing")))), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.Equal(["templates/product.json: a product template cannot hold the section 's2' of type 'custom-listing'"], result.Errors);
    }

    [Fact]
    public async Task SaveAsync_RichTextOfACustomSectionOnAPage_IsSavedSanitized()
    {
        await SaveCatalogAsync();
        _live.Files[Theme($"{Hero}-draft")] = HeroFile;
        var page = new DesignerSavePage
        {
            GroupId = "g1",
            ETag = SiteBuilderContentVersion.Create(await _pages.GetByIdAsync("g1"), Published),
            Content = """{"settings":{},"content":[{"id":"a","type":"custom-hero","body":"<p>Hi</p><img src=x onerror=alert(1)>"}]}""",
        };

        var result = await Operations().SaveAsync(SaveRequest(Save(page)), _user, Ct);

        Assert.True(result.Succeeded, result.Error);
        var saved = await _pages.LoadGroupContentAsync(await _pages.GetByIdAsync("g1"), draft: true, Ct);
        Assert.Equal("<p>Hi</p><img src=\"x\">", JObject.Parse(saved.Content)["content"]![0]!.Value<string>("body"));
    }

    [Theory]
    [InlineData("text", 0)]
    [InlineData("custom-hero", 1)]
    public async Task SaveAsync_OfAPage_ReadsTheThemesTypesOnlyWhenItPlacesOne(string type, int reads)
    {
        await SaveCatalogAsync(sections: """, "text": {}""");
        _live.Files[Theme($"{Hero}-draft")] = HeroFile;
        var service = new CountingService(new CustomSectionService(_themes, new ThemeDraftService(_themes, _lock, _live, NullLogger<ThemeDraftService>.Instance)));
        var page = new DesignerSavePage
        {
            GroupId = "g1",
            ETag = SiteBuilderContentVersion.Create(await _pages.GetByIdAsync("g1"), Published),
            Content = $$"""{"settings":{},"content":[{"id":"a","type":"{{type}}"}]}""",
        };

        var result = await Operations(customSectionService: service).SaveAsync(SaveRequest(Save(page)), _user, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(reads, service.Reads);
    }

    [Fact]
    public async Task SaveAsync_TypesTogetherOverTheLimit_IsInvalid()
    {
        await SaveCatalogAsync();
        var padding = $"/* {new string('x', 60 * 1024)} */";
        foreach (var type in new[] { "custom-a", "custom-b", "custom-c", "custom-d" })
        {
            _live.Files[Theme($"default/custom-sections/{type}.json-draft")] = HeroWith(css: padding);
        }

        var result = await Operations().SaveAsync(SaveRequest(Save(null, (Hero, HeroWith(css: padding)))), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, result.Status);
        Assert.StartsWith("custom-sections: the custom section types are ", Assert.Single(result.Errors));
        Assert.EndsWith("more than 256 KB: the snapshot carries every type to every page", result.Errors[0]);
    }

    [Theory]
    [InlineData(false, SiteBuilderOperationStatus.Succeeded)]
    [InlineData(true, SiteBuilderOperationStatus.Invalid)]
    public async Task SaveAsync_TypesAlreadyOverTheLimit_IsRefusedOnlyWhenItGrowsThem(bool grows, SiteBuilderOperationStatus status)
    {
        await SaveCatalogAsync();
        var padding = $"/* {new string('x', 60 * 1024)} */";
        foreach (var type in new[] { "custom-a", "custom-b", "custom-c", "custom-d", "custom-e", "custom-hero" })
        {
            _live.Files[Theme($"default/custom-sections/{type}.json-draft")] = HeroWith(css: padding);
        }

        var result = await Operations().SaveAsync(SaveRequest(Save(null, (Hero, HeroWith(css: grows ? padding + padding : null)))), _user, Ct);

        Assert.Equal(status, result.Status);
    }

    private static string HeroWith(string template = null, string css = null)
    {
        var hero = JObject.Parse(HeroFile);
        hero["template"] = template ?? hero["template"];
        hero["css"] = css ?? hero["css"];

        return hero.ToString();
    }

    private static string TemplateOf(params string[] types) =>
        new JObject
        {
            ["settings"] = new JObject(),
            ["content"] = new JArray(types.Select((x, i) => new JObject { ["id"] = $"s{i}", ["type"] = x })),
        }.ToString(Newtonsoft.Json.Formatting.None);

    private Task<string> SaveCatalogAsync(string sections = "", string kit = "") =>
        new SchemaCatalogService(_themes).SaveAsync("s1",
            $$"""{ "hash": "{{Catalog}}", "sections": { "product-info": {}{{sections}} }, "blocks": {}, "shared": {}{{kit}} }""", Ct);

    private DesignerContentService Designer() =>
        TestServices.Designer(_history, themeDrafts: new ThemeDraftService(_themes, _lock, _live, NullLogger<ThemeDraftService>.Instance), pages: _pages,
            catalogs: new SchemaCatalogService(_themes), themes: _themes);

    private DesignerOperationService Operations(ICustomSectionValidator customSectionValidator = null, ICustomSectionService customSectionService = null)
    {
        var authorization = new FakeAuthorizationService(true);
        var options = Options.Create(new GitContentOptions());
        var policy = GitHubConnections.CreatePolicy(options, new FakeSettingsManager(false), new FakeGitContentOwnership());
        var filePageDrafts = TestServices.FilePageDrafts(_live, options, GitHubConnections.CreateService(options.Value), policy, null, null, new FakeGitContentProvisioner());
        var themeDrafts = new ThemeDraftService(_themes, _lock, _live, NullLogger<ThemeDraftService>.Instance);
        var themePublisher = new ThemePublisher(_themes, TestServices.ThemeSnapshotBuilder(), new ThemeValidator(), _live.StoreService, new SchemaCatalogService(_themes),
            _lock, _live, new NoContentHistory(), Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, new SiteBuilderContentGuard(), new ThemePaletteService(),
            TestServices.CustomSectionValidator());

        return TestServices.DesignerOperations(
            authorizationService: authorization,
            contentPermissionService: TestServices.Permissions(authorization),
            requestValidator: RequestValidators.Content("s1"),
            designerContentService: TestServices.Designer(_history, filePageDrafts, themeDrafts, _pages, _live, themePublisher, new SchemaCatalogService(_themes), _themes,
                customSectionValidator, customSectionService),
            themeDraftService: themeDrafts,
            groupedPageService: _pages,
            sharedComponentPlacementValidator: new SiteBuilderSharedComponentPlacementValidator(new NoopSharedComponentReferenceIndexService(), authorization));
    }

    private static DesignerSaveOperationRequest SaveRequest(DesignerSaveRequest changes) =>
        new() { StoreId = "s1", Changes = changes, CatalogHash = Catalog, SaveId = "save-0001" };

    private DesignerSaveRequest Save(DesignerSavePage page, params (string Path, string Content)[] themeFiles) => new()
    {
        Page = page,
        ThemeFiles = themeFiles.Select(x => new DesignerSaveFile { Path = x.Path, Content = x.Content, ETag = ETagOf(x.Path) }).ToList(),
    };

    private static string Theme(string path) => $"themes/s1/{path}";

    private string ETagOf(string path) =>
        SiteBuilderContentVersion.CreateForThemeFile("s1", path, _live.Files.GetValueOrDefault(Theme($"{path}-draft")) ?? _live.Files.GetValueOrDefault(Theme(path)));

    private sealed class CountingValidator(ICustomSectionValidator inner) : ICustomSectionValidator
    {
        public int Checked { get; private set; }

        public CustomSectionValidationResult Validate(string type, JToken content, JObject catalog)
        {
            Checked++;
            return inner.Validate(type, content, catalog);
        }

        public IList<CustomSectionValidationResult> ValidateTypes(IDictionary<string, JToken> types, ICollection<string> checkedTypes, JObject catalog)
        {
            Checked += checkedTypes.Count;
            return inner.ValidateTypes(types, checkedTypes, catalog);
        }

        public long GetSize(IDictionary<string, JToken> types) => inner.GetSize(types);

        public CustomSectionRules GetRules() => inner.GetRules();
    }

    private sealed class CountingService(ICustomSectionService inner) : ICustomSectionService
    {
        public int Reads { get; private set; }

        public Task<IList<string>> ListTypesAsync(string storeId, string themeName, CancellationToken cancellationToken = default) =>
            inner.ListTypesAsync(storeId, themeName, cancellationToken);

        public Task<IDictionary<string, JToken>> GetTypesAsync(string storeId, string themeName, IDictionary<string, JToken> files = null,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.GetTypesAsync(storeId, themeName, files, cancellationToken);
        }
    }
}
