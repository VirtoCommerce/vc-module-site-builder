using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.StoreModule.Core.Model;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public sealed class CustomSectionPublishTests : IDisposable
{
    // The snapshot cache region is static and keyed by store id, and other classes publish for "s1" in parallel.
    private const string StoreId = "custom-sections";
    private const string Catalog = "abc12345";
    private const string Product = "default/templates/product.json";
    private const string Hero = "default/custom-sections/custom-hero.json";
    private const string Pointer = "default/snapshots/current.json";
    private const string HeroFile = """
        {
          "format": 1,
          "name": { "en-US": "Hero", "de-DE": "Held" },
          "schema": {
            "settings": [
              { "id": "title", "type": "string", "translatable": true, "default": { "en-US": "Welcome", "de-DE": "Willkommen" } },
              { "id": "body", "type": "text" }
            ],
            "limit": 1
          },
          "template": "<section><h2>{{ settings.title }}</h2><div data-html=\"settings.body\"></div></section>",
          "css": ":scope { color: var(--color-neutral-900); }"
        }
        """;

    private const string Kit = """, "kit": { "tags": { "vc-button": { "attributes": { "label": { "type": "string" } } } }, "behaviors": {} }""";

    private const string CtaFile = """
        {
          "format": 1,
          "name": "Call to action",
          "schema": { "settings": [ { "id": "label", "type": "string", "translatable": true } ] },
          "template": "<section><vc-button label=\"{{ settings.label }}\"></vc-button></section>",
          "css": ""
        }
        """;

    private readonly FakeBlobStorage _storage = new();
    private readonly TestPlatformMemoryCache _cache = new();
    private readonly InProcessDistributedLock _lock = new();
    private readonly FakeStoreService _stores = new(new Store { Id = StoreId, DefaultLanguage = "en-US", Languages = ["en-US", "de-DE"], DynamicProperties = [] });
    private readonly ThemeStorageFactory _storageFactory;
    private readonly ThemePublisher _publisher;
    private readonly ThemeSnapshotService _snapshots;

    public CustomSectionPublishTests()
    {
        _storageFactory = new ThemeStorageFactory(_stores, _storage, _storage, _storage);
        _publisher = new ThemePublisher(_storageFactory, TestServices.ThemeSnapshotBuilder(), new ThemeValidator(), _stores, new SchemaCatalogService(_storageFactory), _lock,
            _storage, new NoContentHistory(), Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, new SiteBuilderContentGuard(),
            new ThemePaletteService(), TestServices.CustomSectionValidator());
        _snapshots = new ThemeSnapshotService(_stores, _storageFactory, _publisher, _cache, Options.Create(new ThemeSnapshotOptions()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task PublishAsync_CustomSectionDraft_GoesLiveAndIntoTheSnapshotOfEachLanguage()
    {
        await SaveCatalogAsync();
        _storage.Files[Theme($"{Hero}-draft")] = HeroFile;
        _storage.Files[Theme($"{Product}-draft")] = TemplateOf("product-info", "custom-hero");

        await _publisher.PublishAsync(StoreId, Catalog, null, Ct);

        Assert.Equal(HeroFile, _storage.Files[Theme(Hero)]);
        Assert.False(_storage.Files.ContainsKey(Theme($"{Hero}-draft")));
        var english = await SnapshotAsync("en-US");
        var german = await SnapshotAsync("de-DE");
        Assert.Equal(["custom-hero"], ((JObject)english["customSections"]!).Properties().Select(x => x.Name));
        Assert.Equal(("Hero", "Welcome"), NameAndDefault(english));
        Assert.Equal(("Held", "Willkommen"), NameAndDefault(german));
        Assert.Equal(JObject.Parse(HeroFile)["template"], english["customSections"]!["custom-hero"]!["template"]);
        Assert.Equal(["product-info", "custom-hero"], english["templates"]!["product"]!["content"]!.Select(x => x.Value<string>("type")));
        Assert.Equal(TestServices.AssetsUrl, english.Value<string>("assetsBaseUrl"));
    }

    [Fact]
    public async Task PublishAsync_ThemeWithoutCustomSections_LeavesThemOutOfTheSnapshot()
    {
        await SaveCatalogAsync();
        _storage.Files[Theme($"{Product}-draft")] = TemplateOf("product-info");

        await _publisher.PublishAsync(StoreId, Catalog, null, Ct);

        Assert.False((await SnapshotAsync("en-US")).ContainsKey("customSections"));
    }

    [Fact]
    public async Task PublishAsync_CustomSectionDraftThatBreaksARule_IsRefusedAndPublishesNothing()
    {
        await SaveCatalogAsync();
        var hero = JObject.Parse(HeroFile);
        hero["css"] = "@import url(\"https://evil.example/x.css\");";
        _storage.Files[Theme($"{Hero}-draft")] = hero.ToString();

        var exception = await Assert.ThrowsAsync<ThemeValidationException>(() => _publisher.PublishAsync(StoreId, Catalog, null, Ct));

        Assert.Contains("custom-sections/custom-hero.json: the CSS has @import, and a custom section's CSS has only @media, @supports, @container, @starting-style and @keyframes",
            exception.Errors);
        Assert.False(_storage.Files.ContainsKey(Theme(Pointer)));
        Assert.False(_storage.Files.ContainsKey(Theme(Hero)));
    }

    [Theory]
    [InlineData(Kit, null)]
    [InlineData("", "custom-sections/custom-hero.json: <vc-button> at line 1 is a kit tag, and the storefront offers none: its catalog has no kit")]
    public async Task PublishAsync_KitTag_IsCheckedAgainstTheKitOfTheCatalogItPublishesWith(string kit, string error)
    {
        await SaveCatalogAsync(kit: kit);
        var hero = JObject.Parse(HeroFile);
        hero["template"] = "<section><vc-button label=\"{{ settings.title }}\"></vc-button></section>";
        _storage.Files[Theme($"{Hero}-draft")] = hero.ToString();

        var exception = await Record.ExceptionAsync(() => _publisher.PublishAsync(StoreId, Catalog, null, Ct));

        if (error == null)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Equal(error, Assert.IsType<ThemeValidationException>(exception).Errors.Single());
        }
    }

    [Fact]
    public async Task PublishAsync_FooterGroupPlacingACustomType_IsRefusedAndPublishesNothing()
    {
        await SaveCatalogAsync();
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme("default/sections/footer-group.json-draft")] = """{ "content": [ { "id": "news", "type": "custom-hero" } ] }""";

        var exception = await Assert.ThrowsAsync<ThemeValidationException>(() => _publisher.PublishAsync(StoreId, Catalog, null, Ct));

        Assert.Equal(["sections/footer-group.json: 'news' places a custom section, which only a page or a template can hold"], exception.Errors);
        Assert.False(_storage.Files.ContainsKey(Theme(Pointer)));
    }

    [Fact]
    public async Task PublishAsync_FileOfTheFolderNamedAsNoType_IsRefused()
    {
        await SaveCatalogAsync();
        _storage.Files[Theme("default/custom-sections/Hero.json-draft")] = HeroFile;

        var exception = await Assert.ThrowsAsync<ThemeValidationException>(() => _publisher.PublishAsync(StoreId, Catalog, null, Ct));

        Assert.Equal(
            ["custom-sections/Hero.json: 'Hero' is not a custom section type, which matches ^custom-[a-z0-9]+(?:-[a-z0-9]+)*$ and has at most 64 characters"],
            exception.Errors);
    }

    [Fact]
    public async Task PublishAsync_TemplatePlacingACustomTypeOnlyThePreviewSaw_IsRefused()
    {
        await SaveCatalogAsync(""", "custom-old": { "settings": [], "custom": true }""");
        _storage.Files[Theme($"{Product}-draft")] = TemplateOf("product-info", "custom-old");

        var exception = await Assert.ThrowsAsync<ThemeValidationException>(() => _publisher.PublishAsync(StoreId, Catalog, null, Ct));

        Assert.Equal(["templates/product.json: 's1' has a type the storefront does not have, 'custom-old'"], exception.Errors);
    }

    [Fact]
    public async Task PublishAsync_RichTextOfACustomSection_GoesLiveSanitized()
    {
        await SaveCatalogAsync();
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme($"{Product}-draft")] =
            """{ "settings": {}, "content": [ { "id": "h", "type": "custom-hero", "body": "<p>Sale</p><img src=x onerror=alert(1)>" } ] }""";

        await _publisher.PublishAsync(StoreId, Catalog, null, Ct);

        Assert.Equal("<p>Sale</p><img src=\"x\">", JObject.Parse(_storage.Files[Theme(Product)])["content"]![0]!.Value<string>("body"));
        Assert.Equal("<p>Sale</p><img src=\"x\">", (await SnapshotAsync("en-US"))["templates"]!["product"]!["content"]![0]!.Value<string>("body"));
    }

    [Fact]
    public async Task FirstPublication_WithoutACatalog_LeavesOutATypeThatUsesTheKit()
    {
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme("default/custom-sections/custom-cta.json")] = CtaFile;

        var snapshot = await SnapshotAsync("en-US");

        Assert.Equal(["custom-hero"], ((JObject)snapshot["customSections"]!).Properties().Select(x => x.Name));
    }

    [Fact]
    public async Task FirstPublication_WithoutACatalog_LeavesACustomTypeOutOfTheHeader()
    {
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme("default/sections/header-group.json")] = """{ "type": "header", "content": [ { "id": "h", "type": "site-header" }, { "id": "promo", "type": "custom-hero" } ] }""";

        var snapshot = await SnapshotAsync("en-US");

        Assert.Equal(["site-header"], snapshot["header"]!["content"]!.Select(x => x.Value<string>("type")));
    }

    [Fact]
    public async Task FirstPublication_WithoutACatalog_LeavesACustomTypeOutOfTheBlocksOfTheHeader()
    {
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme("default/sections/header-group.json")] =
            """{ "type": "header", "content": [ { "id": "h", "type": "site-header", "blocks": [ { "id": "b", "type": "banner" }, { "id": "promo", "type": "custom-hero" } ] } ] }""";

        var snapshot = await SnapshotAsync("en-US");

        Assert.Equal(["banner"], snapshot["header"]!["content"]![0]!["blocks"]!.Select(x => x.Value<string>("type")));
    }

    [Fact]
    public async Task FirstPublication_KeepsABlockNamedLikeACustomTypeTheThemeDoesNotHold()
    {
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme("default/sections/footer-group.json")] =
            """{ "type": "footer", "content": [ { "id": "f", "type": "site-footer", "blocks": [ { "id": "b1", "type": "custom-links" }, { "id": "promo", "type": "custom-hero" } ] } ] }""";

        var snapshot = await SnapshotAsync("en-US");

        Assert.Equal(["custom-links"], snapshot["footer"]!["content"]![0]!["blocks"]!.Select(x => x.Value<string>("type")));
    }

    [Fact]
    public async Task ValidateFilesAsync_TemplatePlacingATypeTheThemeHolds_IsValidWithoutTheTypesFile()
    {
        await SaveCatalogAsync(""", "custom-hero": { "settings": [], "custom": true }""");
        _storage.Files[Theme($"{Hero}-draft")] = HeroFile;

        var exception = await Record.ExceptionAsync(() => _publisher.ValidateFilesAsync(StoreId, Catalog,
            new System.Collections.Generic.Dictionary<string, JToken> { ["templates/product.json"] = JObject.Parse(TemplateOf("product-info", "custom-hero")) }, Ct));

        Assert.Null(exception);
    }

    [Fact]
    public async Task FirstPublication_TypesOverTheLimit_AreEachMeasuredOnce()
    {
        var validator = new CountingValidator(TestServices.CustomSectionValidator(options: new ThemeSnapshotOptions { CustomSectionsMaxSize = 1024 }));
        var publisher = new ThemePublisher(_storageFactory, TestServices.ThemeSnapshotBuilder(), new ThemeValidator(), _stores, new SchemaCatalogService(_storageFactory),
            _lock, _storage, new NoContentHistory(), Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, new SiteBuilderContentGuard(),
            new ThemePaletteService(), validator);
        _storage.Files[Theme(Hero)] = HeroFile;
        var large = JObject.Parse(HeroFile);
        large["css"] = $":scope {{ color: var(--color-neutral-900); }} /* {new string('x', 2000)} */";
        foreach (var type in new[] { "custom-a", "custom-b", "custom-c" })
        {
            _storage.Files[Theme($"default/custom-sections/{type}.json")] = large.ToString();
        }

        await publisher.PublishFirstAsync(StoreId, Ct);

        Assert.Equal(["custom-hero"], ((JObject)(await SnapshotAsync("en-US"))["customSections"]!).Properties().Select(x => x.Name));
        Assert.Equal(4, validator.Sized);
    }

    [Fact]
    public async Task FirstPublication_LeavesOutTheTypeItsOwnResultFails_InWhateverOrderTheResultsCome()
    {
        var publisher = new ThemePublisher(_storageFactory, TestServices.ThemeSnapshotBuilder(), new ThemeValidator(), _stores, new SchemaCatalogService(_storageFactory),
            _lock, _storage, new NoContentHistory(), Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, new SiteBuilderContentGuard(),
            new ThemePaletteService(), new ReversingValidator(TestServices.CustomSectionValidator()));
        var broken = JObject.Parse(HeroFile);
        broken["template"] = "<section onclick=\"go()\"></section>";
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme("default/custom-sections/custom-broken.json")] = broken.ToString();

        await publisher.PublishFirstAsync(StoreId, Ct);

        Assert.Equal(["custom-hero"], ((JObject)(await SnapshotAsync("en-US"))["customSections"]!).Properties().Select(x => x.Name));
    }

    [Fact]
    public async Task FirstPublication_LeavesOutATypeTheContentGuardRefuses()
    {
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme("default/custom-sections/custom-promo.json")] = """
            {
              "format": 1,
              "name": "Promo",
              "schema": {
                "settings": [ { "id": "cta", "type": "object", "element": [ { "id": "link", "type": "string" } ], "default": { "link": "javascript:void(0)" } } ]
              },
              "template": "<section><p>{{ settings.cta.link }}</p></section>",
              "css": ""
            }
            """;

        var snapshot = await SnapshotAsync("en-US");

        Assert.Equal(["custom-hero"], ((JObject)snapshot["customSections"]!).Properties().Select(x => x.Name));
    }

    [Fact]
    public async Task PublishAsync_TypesTogetherOverTheLimit_IsRefusedAndPublishesNothing()
    {
        await SaveCatalogAsync();
        var publisher = new ThemePublisher(_storageFactory, TestServices.ThemeSnapshotBuilder(), new ThemeValidator(), _stores, new SchemaCatalogService(_storageFactory),
            _lock, _storage, new NoContentHistory(), Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, new SiteBuilderContentGuard(),
            new ThemePaletteService(), TestServices.CustomSectionValidator(options: new ThemeSnapshotOptions { CustomSectionsMaxSize = 3 * 1024 }));
        var padded = JObject.Parse(HeroFile);
        padded["css"] = $":scope {{ color: var(--color-neutral-900); }} /* {new string('x', 2000)} */";
        // each under the limit on its own: the live type and the draft only together are over it
        _storage.Files[Theme("default/custom-sections/custom-live.json")] = padded.ToString();
        _storage.Files[Theme($"{Hero}-draft")] = padded.ToString();

        var exception = await Assert.ThrowsAsync<ThemeValidationException>(() => publisher.PublishAsync(StoreId, Catalog, null, Ct));

        Assert.Contains(exception.Errors, x => x.StartsWith("custom-sections: the custom section types are ", StringComparison.Ordinal));
        Assert.False(_storage.Files.ContainsKey(Theme(Pointer)));
        Assert.False(_storage.Files.ContainsKey(Theme(Hero)));
        Assert.True(_storage.Files.ContainsKey(Theme($"{Hero}-draft")));
    }

    [Fact]
    public async Task FirstPublication_LeavesOutTheLargestTypesUntilTheRestFit()
    {
        var publisher = new ThemePublisher(_storageFactory, TestServices.ThemeSnapshotBuilder(), new ThemeValidator(), _stores, new SchemaCatalogService(_storageFactory),
            _lock, _storage, new NoContentHistory(), Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, new SiteBuilderContentGuard(),
            new ThemePaletteService(), TestServices.CustomSectionValidator(options: new ThemeSnapshotOptions { CustomSectionsMaxSize = 1024 }));
        _storage.Files[Theme(Hero)] = HeroFile;
        var large = JObject.Parse(HeroFile);
        large["css"] = $":scope {{ color: var(--color-neutral-900); }} /* {new string('x', 2000)} */";
        _storage.Files[Theme("default/custom-sections/custom-large.json")] = large.ToString();

        await publisher.PublishFirstAsync(StoreId, Ct);

        Assert.Equal(["custom-hero"], ((JObject)(await SnapshotAsync("en-US"))["customSections"]!).Properties().Select(x => x.Name));
    }

    [Fact]
    public async Task FirstPublication_LeavesOutTheLargestTypesUntilTheRestFitTheCount()
    {
        foreach (var type in Enumerable.Range(0, CustomSectionValidator.MaxTypes - 1).Select(x => $"custom-t{x}"))
        {
            _storage.Files[Theme($"default/custom-sections/{type}.json")] = HeroFile;
        }

        var large = JObject.Parse(HeroFile);
        large["css"] = $":scope {{ color: var(--color-neutral-900); }} /* {new string('x', 100)} */";
        // listed out of order, so only the order of the types decides which of the equally large ones stays
        _storage.Files[Theme("default/custom-sections/custom-large-c.json")] = large.ToString();
        _storage.Files[Theme("default/custom-sections/custom-large-a.json")] = large.ToString();
        _storage.Files[Theme("default/custom-sections/custom-large-b.json")] = large.ToString();

        var types = ((JObject)(await SnapshotAsync("en-US"))["customSections"]!).Properties().Select(x => x.Name).ToList();

        Assert.Equal(CustomSectionValidator.MaxTypes, types.Count);
        Assert.Equal(["custom-large-c"], types.Where(x => x.StartsWith("custom-large-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PublishAsync_MoreTypesThanTheLimit_IsRefused()
    {
        await SaveCatalogAsync();
        foreach (var type in Enumerable.Range(0, CustomSectionValidator.MaxTypes + 1).Select(x => $"custom-t{x}"))
        {
            _storage.Files[Theme($"default/custom-sections/{type}.json-draft")] = HeroFile;
        }

        var exception = await Assert.ThrowsAsync<ThemeValidationException>(() => _publisher.PublishAsync(StoreId, Catalog, null, Ct));

        Assert.Contains("custom-sections: the theme holds 257 custom section types, more than 256: the snapshot carries every type to every page", exception.Errors);
    }

    [Fact]
    public async Task ValidateFilesAsync_TemplatePlacingTypesTheThemeDoesNotHold_ReadsOnlyTheTypesItHolds()
    {
        await SaveCatalogAsync();
        _storage.Files[Theme(Hero)] = HeroFile;
        var template = TemplateOf([.. Enumerable.Range(0, 200).Select(x => $"custom-planted-{x}"), "custom-hero"]);

        await Record.ExceptionAsync(() => _publisher.ValidateFilesAsync(StoreId, Catalog,
            new System.Collections.Generic.Dictionary<string, JToken> { ["templates/product.json"] = JToken.Parse(template) }, Ct));

        Assert.DoesNotContain(_storage.LookedUp, x => x.Contains("/custom-planted-", StringComparison.Ordinal));
        Assert.Equal([Theme(Hero)], _storage.Read.Where(x => x.Contains("/default/custom-sections/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ValidateFilesAsync_RequestCancelled_ReadsNoType()
    {
        await SaveCatalogAsync();
        _storage.Files[Theme(Hero)] = HeroFile;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _publisher.ValidateFilesAsync(StoreId, Catalog,
            new System.Collections.Generic.Dictionary<string, JToken> { ["templates/product.json"] = JToken.Parse(TemplateOf("custom-hero")) }, cancelled.Token));

        Assert.DoesNotContain(_storage.LookedUp, x => x.Contains("/default/custom-sections/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FirstPublication_LeavesOutTheTypesLargestInBytes_AsTheLimitCountsThem()
    {
        var publisher = new ThemePublisher(_storageFactory, TestServices.ThemeSnapshotBuilder(), new ThemeValidator(), _stores, new SchemaCatalogService(_storageFactory),
            _lock, _storage, new NoContentHistory(), Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, new SiteBuilderContentGuard(),
            new ThemePaletteService(), TestServices.CustomSectionValidator(options: new ThemeSnapshotOptions { CustomSectionsMaxSize = 2048 }));
        // more characters, but fewer bytes in UTF-8
        var latin = JObject.Parse(HeroFile);
        latin["css"] = $":scope {{ color: var(--color-neutral-900); }} /* {new string('x', 1000)} */";
        var euro = JObject.Parse(HeroFile);
        euro["css"] = $":scope {{ color: var(--color-neutral-900); }} /* {new string('€', 600)} */";
        _storage.Files[Theme("default/custom-sections/custom-latin.json")] = latin.ToString();
        _storage.Files[Theme("default/custom-sections/custom-euro.json")] = euro.ToString();

        await publisher.PublishFirstAsync(StoreId, Ct);

        Assert.Equal(["custom-latin"], ((JObject)(await SnapshotAsync("en-US"))["customSections"]!).Properties().Select(x => x.Name));
    }

    [Fact]
    public async Task FirstPublication_ChecksEachTypeOnce_ReadingTheKitOnce()
    {
        var validator = new CountingValidator(TestServices.CustomSectionValidator());
        var publisher = new ThemePublisher(_storageFactory, TestServices.ThemeSnapshotBuilder(), new ThemeValidator(), _stores, new SchemaCatalogService(_storageFactory),
            _lock, _storage, new NoContentHistory(), Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, new SiteBuilderContentGuard(),
            new ThemePaletteService(), validator);
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme("default/custom-sections/custom-promo.json")] = HeroFile;

        await publisher.PublishFirstAsync(StoreId, Ct);

        Assert.Equal((0, 1, 2), (validator.Validated, validator.Calls, validator.Checked));
    }

    [Fact]
    public async Task FirstPublication_LeavesOutATypeThatIsNotJson()
    {
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme("default/custom-sections/custom-broken.json")] = "{ \"format\": 1,";

        var snapshot = await SnapshotAsync("en-US");

        Assert.Equal(["custom-hero"], ((JObject)snapshot["customSections"]!).Properties().Select(x => x.Name));
    }

    [Fact]
    public async Task PublishAsync_TypeDraftThatIsNotJson_IsRefused()
    {
        await SaveCatalogAsync();
        _storage.Files[Theme("default/custom-sections/custom-broken.json-draft")] = "{ \"format\": 1,";

        var exception = await Assert.ThrowsAsync<ThemeValidationException>(() => _publisher.PublishAsync(StoreId, Catalog, null, Ct));

        Assert.Contains("custom-sections/custom-broken.json: not valid JSON", exception.Errors);
    }

    [Fact]
    public async Task FirstPublication_ChecksTheTypesAgainstTheCurrentCatalog()
    {
        await SaveCatalogAsync(kit: Kit);
        _storage.Files[Theme(Hero)] = HeroFile;
        _storage.Files[Theme("default/custom-sections/custom-cta.json")] = CtaFile;
        _storage.Files[Theme("default/custom-sections/custom-badge.json")] = CtaFile.Replace("vc-button", "vc-badge", StringComparison.Ordinal);

        var snapshot = await SnapshotAsync("en-US");

        Assert.Equal(["custom-cta", "custom-hero"], ((JObject)snapshot["customSections"]!).Properties().Select(x => x.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetStatusAsync_CustomSectionDraft_IsListedAsANewDraft()
    {
        _storage.Files[Theme($"{Hero}-draft")] = HeroFile;

        var status = await _publisher.GetStatusAsync(StoreId, Ct);

        Assert.Equal(["custom-sections/custom-hero.json"], status.Drafts);
        Assert.Equal(["custom-sections/custom-hero.json"], status.NewDrafts);
    }

    private async Task<JObject> SnapshotAsync(string language) => JObject.Parse((await _snapshots.GetPublishedAsync(StoreId, language, Ct)).Content);

    private static (string Name, string Default) NameAndDefault(JObject snapshot)
    {
        var hero = snapshot["customSections"]!["custom-hero"]!;

        return (hero.Value<string>("name"), hero["schema"]!["settings"]![0]!.Value<string>("default"));
    }

    private Task<string> SaveCatalogAsync(string sections = "", string kit = "") =>
        new SchemaCatalogService(_storageFactory).SaveAsync(StoreId,
            $$"""{ "hash": "{{Catalog}}", "sections": { "product-info": {}{{sections}} }, "blocks": {}, "shared": {}{{kit}} }""", Ct);

    private static string Theme(string path) => $"themes/{StoreId}/{path}";

    private static string TemplateOf(params string[] types) =>
        new JObject
        {
            ["settings"] = new JObject(),
            ["content"] = new JArray(types.Select((x, i) => new JObject { ["id"] = $"s{i}", ["type"] = x })),
        }.ToString(Newtonsoft.Json.Formatting.None);

    private sealed class CountingValidator(ICustomSectionValidator inner) : ICustomSectionValidator
    {
        public int Validated { get; private set; }

        public int Calls { get; private set; }

        public int Checked { get; private set; }

        public int Sized { get; private set; }

        public CustomSectionValidationResult Validate(string type, JToken content, JObject catalog)
        {
            Validated++;
            return inner.Validate(type, content, catalog);
        }

        public System.Collections.Generic.IList<CustomSectionValidationResult> ValidateTypes(System.Collections.Generic.IDictionary<string, JToken> types,
            System.Collections.Generic.ICollection<string> checkedTypes, JObject catalog)
        {
            Calls += checkedTypes.Count > 0 ? 1 : 0;
            Checked += checkedTypes.Count;
            Sized += checkedTypes.Count > 0 ? 0 : 1;
            return inner.ValidateTypes(types, checkedTypes, catalog);
        }

        public long GetSize(System.Collections.Generic.IDictionary<string, JToken> types)
        {
            Sized++;
            return inner.GetSize(types);
        }

        public CustomSectionRules GetRules() => inner.GetRules();
    }

    private sealed class ReversingValidator(ICustomSectionValidator inner) : ICustomSectionValidator
    {
        public CustomSectionValidationResult Validate(string type, JToken content, JObject catalog) => inner.Validate(type, content, catalog);

        public System.Collections.Generic.IList<CustomSectionValidationResult> ValidateTypes(System.Collections.Generic.IDictionary<string, JToken> types,
            System.Collections.Generic.ICollection<string> checkedTypes, JObject catalog) =>
            inner.ValidateTypes(types, checkedTypes, catalog).Reverse().ToList();

        public long GetSize(System.Collections.Generic.IDictionary<string, JToken> types) => inner.GetSize(types);

        public CustomSectionRules GetRules() => inner.GetRules();
    }
}
