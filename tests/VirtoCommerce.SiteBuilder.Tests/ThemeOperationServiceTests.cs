using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using VirtoCommerce.StoreModule.Core.Model;
using Xunit;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.ChangePreview;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests;

public class ThemeOperationServiceTests
{
    // The snapshot cache region is static and keyed by store id, and other classes publish in parallel.
    private const string StoreId = "theme-operations";
    private const string Catalog = "catalog-product-info";
    private const string Product = "default/templates/product.json";
    private const string Palette = "default/config/palette.json";
    private const string DarkPalette = "default/config/palette.dark.json";
    private const string Pointer = "default/snapshots/current.json";
    private const string PassingPalette = """{ "color_neutral_950": "#111111", "color_neutral_50": "#ffffff" }""";
    // #777777 on white is 4.47:1, short of the 4.5:1 body text needs
    private const string FailingPalette = """{ "color_neutral_950": "#777777", "color_neutral_50": "#ffffff" }""";
    private const string FailingPair = "'body-text' has a contrast of 4.47:1 (color_neutral_950 #777777 on color_neutral_50 #ffffff), less than the 4.5:1 WCAG 2.2 AA asks for";

    private readonly FakeBlobStorage _storage = new();
    private readonly InProcessDistributedLock _lock = new();
    private readonly NewSiteBuilder _newSiteBuilder = new();
    private readonly FakeStoreService _stores = new(new Store { Id = StoreId, DefaultLanguage = "en-US", Languages = ["en-US"], DynamicProperties = [] });
    private readonly ThemeStorageFactory _themes;

    public ThemeOperationServiceTests()
    {
        _themes = new ThemeStorageFactory(_stores, _storage, _storage, _storage);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User { get; } = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

    [Fact]
    public async Task PublishAsync_UserOfAnotherStoreSendingABadRequest_IsForbiddenBeforeTheRequestIsChecked()
    {
        var request = new ThemePublishOperationRequest { StoreId = StoreId, CatalogHash = "bad", BindingErrors = ["The value is not valid."] };

        var result = await Service(new FakeAuthorizationService("other", Permissions.Publish)).PublishAsync(request, User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
        Assert.Equal(0, _newSiteBuilder.Asked);
    }

    [Fact]
    public async Task PublishAsync_ValueThatDidNotBind_IsInvalidBeforeTheStoreIsAskedForTheNewSiteBuilder()
    {
        var request = new ThemePublishOperationRequest { StoreId = StoreId, CatalogHash = Catalog, BindingErrors = ["The value is not valid."] };

        var result = await Service().PublishAsync(request, User, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Invalid, "The value is not valid."), (result.Status, result.Error));
        Assert.Equal(0, _newSiteBuilder.Asked);
    }

    [Fact]
    public async Task PublishAsync_DryRun_AnswersEveryDraftAtItsVersionAndWritesNothing()
    {
        await SeedThemeAsync();
        _storage.Files[Theme(Palette + "-draft")] = PassingPalette;
        var history = new AuthoredHistory();
        history.Versions[Product] = new ContentVersion { VersionId = "v1", Author = new ContentVersionAuthor { Name = "Bob", Login = "bob" }, Date = new DateTime(2026, 11, 1) };
        var before = new Dictionary<string, string>(_storage.Files);
        _storage.Written.Clear();

        var result = await Service(history: history).PublishAsync(DryRun(), User, Ct);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value);
        Assert.True(result.AffectsPublished);
        Assert.True(result.Preview.AffectsPublished);
        var drafts = result.Preview.Objects.OrderBy(x => x.ObjectId, StringComparer.Ordinal).ToList();
        Assert.Equal([Palette, Product], drafts.Select(x => x.ObjectId));
        Assert.All(drafts, x => Assert.Equal(ThemeFile, x.ObjectType));
        Assert.All(drafts, x => Assert.Equal(("status", "Draft", "Published"), (x.Fields.Single().Name, x.Fields.Single().From, x.Fields.Single().To)));
        Assert.Equal(await VersionOfAsync(Palette), drafts[0].ETag);
        Assert.Equal(await VersionOfAsync(Product), drafts[1].ETag);
        Assert.Null(drafts[0].ChangedBy);
        Assert.Equal(("Bob", new DateTime(2026, 11, 1)), (drafts[1].ChangedBy.Name, drafts[1].ChangedDate));
        Assert.Equal([$"The history records no author of the draft of {Palette}."], result.Preview.Warnings);
        Assert.Equal(before, _storage.Files);
        Assert.Empty(_storage.Written);
        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task PublishAsync_DryRunOfAStoreWithoutHistory_SaysWhoChangedTheDraftsIsNotKnown()
    {
        await SeedThemeAsync();

        var result = await Service().PublishAsync(DryRun(), User, Ct);

        Assert.Null(Assert.Single(result.Preview.Objects).ChangedBy);
        Assert.Equal(["The store keeps no content history, so who last changed the drafts is not known."], result.Preview.Warnings);
    }

    [Fact]
    public async Task PublishAsync_DraftsAtTheVersionsReviewed_PublishesThem()
    {
        await SeedThemeAsync();
        var reviewed = await Service().PublishAsync(DryRun(), User, Ct);

        var result = await Service().PublishAsync(Publish(reviewed.Preview.Objects.ToDictionary(x => x.ObjectId, x => x.ETag)), User, Ct);

        Assert.True(result.Succeeded);
        Assert.True(result.AffectsPublished);
        Assert.Equal(1, result.Value.Publication);
        Assert.False(_storage.Files.ContainsKey(Theme(Product + "-draft")));
    }

    [Fact]
    public async Task PublishAsync_DraftChangedSinceItWasReviewed_IsAConflictAndPublishesNothing()
    {
        await SeedThemeAsync();
        var reviewed = await Service().PublishAsync(DryRun(), User, Ct);
        _storage.Files[Theme(Product + "-draft")] = Template("product-info", "text");

        var result = await Service().PublishAsync(Publish(reviewed.Preview.Objects.ToDictionary(x => x.ObjectId, x => x.ETag)), User, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Conflict, SiteBuilderContentConflictException.ThemeDraftsMessage), (result.Status, result.Error));
        AssertNothingPublished();
    }

    [Fact]
    public async Task PublishAsync_DraftAddedSinceTheReview_IsAConflictAndPublishesNothing()
    {
        await SeedThemeAsync();
        var reviewed = await Service().PublishAsync(DryRun(), User, Ct);
        _storage.Files[Theme(Palette + "-draft")] = PassingPalette;

        var result = await Service().PublishAsync(Publish(reviewed.Preview.Objects.ToDictionary(x => x.ObjectId, x => x.ETag)), User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        AssertNothingPublished();
    }

    [Fact]
    public async Task PublishAsync_DraftPublishedSinceTheReview_IsAConflict()
    {
        await SeedThemeAsync();
        var expected = new Dictionary<string, string> { [Product] = await VersionOfAsync(Product), [Palette] = "gone since" };

        var result = await Service().PublishAsync(Publish(expected), User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        AssertNothingPublished();
    }

    [Fact]
    public async Task PublishAsync_PalettesThatMeetTheContrast_PublishesThem()
    {
        await SeedThemeAsync();
        _storage.Files[Theme(Palette + "-draft")] = PassingPalette;
        _storage.Files[Theme(DarkPalette + "-draft")] = """{ "color_neutral_950": "#f5f5f5", "color_neutral_50": "#0a0a0a" }""";

        var result = await Service().PublishAsync(Publish(), User, Ct);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_PaletteThatFailsTheContrast_IsRefusedWithTheFailingPairs(bool dark)
    {
        await SeedThemeAsync();
        _storage.Files[Theme(Palette)] = dark ? PassingPalette : FailingPalette;
        _storage.Files[Theme(DarkPalette + "-draft")] = dark ? FailingPalette : PassingPalette;

        var result = await Service().PublishAsync(Publish(), User, Ct);
        var dryRun = await Service().PublishAsync(DryRun(), User, Ct);

        var error = $"config/{(dark ? "palette.dark.json" : "palette.json")}: {FailingPair}";
        Assert.Equal((SiteBuilderOperationStatus.Invalid, $"The theme is not valid: {error}"), (result.Status, result.Error));
        Assert.Equal([error], result.Errors);
        Assert.Equal(SiteBuilderOperationStatus.Invalid, dryRun.Status);
        Assert.Equal([error], dryRun.Errors);
        AssertNothingPublished();
    }

    [Fact]
    public async Task PublishAsync_DarkPaletteWithoutALightOne_IsNotChecked()
    {
        // without light colors the snapshot has no palette, and the storefront keeps its preset
        await SeedThemeAsync();
        _storage.Files[Theme(DarkPalette + "-draft")] = FailingPalette;

        var result = await Service().PublishAsync(Publish(), User, Ct);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task PublishAsync_PaletteTokenTheStorefrontAnnouncesAndThePaletteLeavesOut_IsNotCheckedAgainstItsFallback()
    {
        await SeedThemeAsync("""{ "light": [ "color_body_text", "color_neutral_950", "color_neutral_50" ], "dark": [] }""");
        _storage.Files[Theme(Palette + "-draft")] = FailingPalette;

        var result = await Service().PublishAsync(Publish(), User, Ct);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Publish_PaletteThatFailsTheContrast_AnswersTheFailingPairsAsTheOtherThemeErrors()
    {
        await SeedThemeAsync();
        _storage.Files[Theme(Palette + "-draft")] = FailingPalette;

        var result = await Controller().Publish(StoreId, Catalog, Ct);

        var error = $"config/palette.json: {FailingPair}";
        Assert.Equal($$"""{"error":"The theme is not valid: {{error}}","errors":["{{error}}"]}""",
            PlatformJson.SerializeBody(Assert.IsType<BadRequestObjectResult>(result.Result)));
    }

    [Fact]
    public async Task Publish_EndpointSendsNoVersions_PublishesTheDraftsAsTheyAre()
    {
        await SeedThemeAsync();

        var result = await Controller().Publish(StoreId, Catalog, Ct);

        Assert.Equal("""{"publication":1}""", PlatformJson.SerializeBody(result));
    }

    [Fact]
    public async Task GetStatusAsync_UserOfAnotherStoreSendingABadRequest_IsForbidden()
    {
        var request = new SiteBuilderOperationRequest { StoreId = "unknown", BindingErrors = ["The value is not valid."] };

        var result = await Service(new FakeAuthorizationService("other", Permissions.Read)).GetStatusAsync(request, User, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task GetStatusAsync_StoreUserWorksIn_AnswersTheDrafts()
    {
        await SeedThemeAsync();

        var result = await Service().GetStatusAsync(new SiteBuilderOperationRequest { StoreId = StoreId }, User, Ct);

        Assert.Equal(["templates/product.json"], result.Value.Drafts);
        Assert.Equal(["templates/product.json"], result.Value.NewDrafts);
    }

    private async Task SeedThemeAsync(string paletteTokens = null)
    {
        var catalog = new JObject { ["hash"] = Catalog, ["sections"] = new JObject { ["product-info"] = new JObject(), ["text"] = new JObject() } };
        if (paletteTokens != null)
        {
            catalog["paletteTokens"] = JObject.Parse(paletteTokens);
        }

        await new SchemaCatalogService(_themes).SaveAsync(StoreId, catalog.ToString(), Ct);
        _storage.Files[Theme(Product + "-draft")] = Template("product-info");
    }

    private void AssertNothingPublished()
    {
        Assert.False(_storage.Files.ContainsKey(Theme(Pointer)));
        Assert.True(_storage.Files.ContainsKey(Theme(Product + "-draft")));
    }

    private async Task<string> VersionOfAsync(string path) =>
        (await new ThemeDraftService(_themes, _lock, _storage, NullLogger<ThemeDraftService>.Instance).ReadAsync(StoreId, path, Ct)).ETag;

    private static ThemePublishOperationRequest DryRun() => new() { StoreId = StoreId, CatalogHash = Catalog, DryRun = true };

    private static ThemePublishOperationRequest Publish(IDictionary<string, string> expectedVersions = null) =>
        new() { StoreId = StoreId, CatalogHash = Catalog, ExpectedVersions = expectedVersions };

    private ThemeOperationService Service(FakeAuthorizationService authorization = null, IContentHistory history = null) =>
        TestServices.ThemeOperations(authorization ?? new FakeAuthorizationService(true), _newSiteBuilder, Publisher(), history, StoreId);

    private ThemePublisher Publisher() =>
        new(_themes, new ThemeSnapshotBuilder(), new ThemeValidator(), _stores, new SchemaCatalogService(_themes), _lock, _storage, new NoContentHistory(),
            Options.Create(new ThemeSnapshotOptions()), NullLogger<ThemePublisher>.Instance, new SiteBuilderContentGuard(), new ThemePaletteService());

    private ThemeController Controller() =>
        new(Service(), new SchemaCatalogService(_themes), new ThemePaletteService(), new FakeAuthorizationService(true), RequestValidators.Stores(StoreId),
            new ThemePaletteGenerationRequestValidator(new ThemePaletteTokensValidator()), new ThemePaletteCheckRequestValidator(new ThemePaletteTokensValidator()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User } },
        };

    private static string Theme(string path) => $"themes/{StoreId}/{path}";

    private static string Template(params string[] sections) =>
        new JObject
        {
            ["settings"] = new JObject { ["type"] = "settings" },
            ["content"] = new JArray(sections.Select((type, i) => new JObject { ["id"] = $"s{i}", ["type"] = type })),
        }.ToString();

    private sealed class NewSiteBuilder : IThemeSnapshotService
    {
        public int Asked { get; private set; }

        public Task<ThemeSnapshot> GetPublishedAsync(string storeId, string cultureName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsNewSiteBuilderEnabledAsync(string storeId, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(true);
        }
    }

    /// <summary>The last version of each theme file, by its path with the theme folder.</summary>
    private sealed class AuthoredHistory : IContentHistory
    {
        public Dictionary<string, ContentVersion> Versions { get; } = [];

        public List<ContentHistoryRecord> Records { get; } = [];

        public Task<ContentHistoryStatus> GetStatusAsync(string storeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ContentHistoryStatus.Enabled);

        public Task RecordAsync(string storeId, ContentHistoryRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<ContentVersionPage> ListAsync(string storeId, IList<HistoryItem> items, string cursor, int take, CancellationToken cancellationToken = default)
        {
            var page = new ContentVersionPage { Status = ContentHistoryStatus.Enabled };
            if (Versions.TryGetValue(items.Single().Key, out var version))
            {
                page.Versions.Add(version);
            }

            return Task.FromResult(page);
        }

        public Task<string> ReadAsync(string storeId, HistoryItem item, string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string>(null);
    }
}
