using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP.Services;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class CreatePageToolTests : IAsyncDisposable
{
    private const string Tool = "create_page";

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;

    public CreatePageToolTests()
    {
        _pages = _context.UsePageStore();
        _context.SeedPage("a1", StoreA, published: true, draft: true);
        _context.SeedPage("b1", StoreB, published: true, draft: false);
        _context.PageList
            .Setup(x => x.GetCopyNameAsync(It.IsAny<GroupedSiteBuilderPage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupedSiteBuilderPage page, CancellationToken _) => new PageCopyName { Name = $"{page.Name} (copy)", Permalink = $"{page.Permalink}-copy" });
        _context.AddMultilanguagePages(Root("ml-a", StoreA), Root("ml-b", StoreB));
        _context.MultilanguagePages
            .Setup(x => x.GetLanguagesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Author() => McpTestUser.Create("author").WithPermissions(Permissions.Read, Permissions.Create);

    private static McpTestUser StoreAAuthor() =>
        McpTestUser.Create("store-a-author").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Create);

    [Fact]
    public void TheTool_NeedsBuilderReadAndCreate()
    {
        var tool = _context.Harness.GetTool(Tool);

        Assert.True(_context.Harness.IsVisible(tool, Author()));
        Assert.True(_context.Harness.IsVisible(tool, StoreAAuthor()));
        Assert.False(_context.Harness.IsVisible(tool, Reader()));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("creator").WithPermissions(Permissions.Create)));
    }

    [Fact]
    public async Task ALandingPage_IsCreatedAtOnce_WithItsSections()
    {
        var result = await _context.CallAsync(Author(), Tool, new
        {
            store_id = StoreA,
            name = "Spring sale",
            permalink = "/spring-sale",
            language = "en-US",
            sections = new object[] { new { op = "add_section", type = "hero-banner", settings = new { title = "Spring sale" } } },
        });

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        Assert.Equal(McpToolCallOutcome.Succeeded, result.Outcome);
        Assert.False(change.DryRun);
        Assert.False(change.AffectsPublished);

        var created = await _pages.GetByIdAsync(change.PageId);
        Assert.Equal((StoreA, "Spring sale", "/spring-sale", "Draft"), (created.StoreId, created.Name, created.Permalink, created.Status));
        var content = JsonNode.Parse((await _pages.LoadGroupContentAsync(created, cancellationToken: CancellationToken.None)).Content)!;
        Assert.Equal("Spring sale", content["content"]![0]!["title"]!.GetValue<string>());
        Assert.Equal("/spring-sale", content["settings"]!["permalink"]!.GetValue<string>());
        Assert.Equal(["hero-banner"], change.Sections.Select(x => x.Type));
    }

    [Theory]
    [InlineData(false, false, "the theme does not hold")]
    [InlineData(true, false, "the theme holds only as a draft")]
    [InlineData(false, true, "the theme does not hold")]
    [InlineData(true, true, "the theme holds only as a draft")]
    public async Task ASectionOfACustomSectionTypeThePublishedThemeLacks_IsCreatedWithAWarning(bool draftType, bool dryRun, string why)
    {
        await _context.SeedThemeAsync(StoreA);
        if (draftType)
        {
            _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType, draft: true);
        }

        var change = (await _context.CallAsync(Author(), Tool, new
        {
            store_id = StoreA,
            name = "Stats",
            permalink = "/stats",
            sections = new object[] { new { op = "add_section", type = "custom-stats" } },
            dryRun,
        })).EnsureSucceeded().Read<SiteBuilderChange>();

        var warning = Assert.Single(change.Warnings, x => x.Contains("(custom-stats)", StringComparison.Ordinal));
        Assert.StartsWith($"Page 'Stats' places custom section types {why} (custom-stats): it can still be published, but the storefront shows nothing in their place",
            warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACopyOfAPageThatPlacesACustomSectionTypeTheThemeDoesNotHold_IsCreatedWithAWarning()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedDraft("stats", StoreA, """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""");

        var change = (await _context.CallAsync(Author(), Tool, new { copy_of_page_id = "stats", name = "Stats copy", permalink = "/stats-copy" }))
            .EnsureSucceeded().Read<SiteBuilderChange>();

        Assert.Contains(change.Warnings, x => x.StartsWith("Page 'Stats copy' places custom section types the theme does not hold (custom-stats)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASectionOfAPublishedCustomSectionType_IsCreatedWithoutASectionTypeWarning()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "custom-sections/custom-stats.json", StatsSectionType);

        var change = (await _context.CallAsync(Author(), Tool, new
        {
            store_id = StoreA,
            name = "Stats",
            permalink = "/stats",
            sections = new object[] { new { op = "add_section", type = "custom-stats" } },
        })).EnsureSucceeded().Read<SiteBuilderChange>();

        Assert.DoesNotContain(change.Warnings, x => x.Contains("custom-stats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADryRun_CreatesNothing_AndShowsThePage()
    {
        var result = await _context.CallAsync(Author(), Tool, new { store_id = StoreA, name = "Spring sale", permalink = "/spring-sale", dryRun = true });

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        Assert.True(change.DryRun);
        Assert.Null(change.PageId);
        Assert.Contains(change.Changes, x => x.Field == "name" && x.To?.ToString() == "Spring sale");
        Assert.Equal(2, (await _pages.GetAsync(["a1", "b1"])).Count);
        Assert.Null(await _pages.GetByIdAsync("group-new-1"));
    }

    [Fact]
    public async Task ACopy_TakesTheSourceDraftAudienceAndACopyName()
    {
        var result = await _context.CallAsync(Author(), Tool, new { copy_of_page_id = "a1" });

        var created = await _pages.GetByIdAsync(result.EnsureSucceeded().Read<SiteBuilderChange>().PageId);
        Assert.Equal((StoreA, "Page a1 (copy)", "/a1-copy"), (created.StoreId, created.Name, created.Permalink));
        var content = JsonNode.Parse((await _pages.LoadGroupContentAsync(created, cancellationToken: CancellationToken.None)).Content)!;
        Assert.Equal("Summer", content["content"]![0]!["title"]!.GetValue<string>());
        Assert.Equal("Page a1 (copy)", content["settings"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task AUserOfStoreA_CannotCreateInStoreB_NorCopyItsPages()
    {
        var inStoreB = await _context.CallAsync(StoreAAuthor(), Tool, new { store_id = StoreB, name = "Sale" });
        var copyOfB = await _context.CallAsync(StoreAAuthor(), Tool, new { copy_of_page_id = "b1", store_id = StoreA });
        var inStoreA = await _context.CallAsync(StoreAAuthor(), Tool, new { store_id = StoreA, name = "Sale", permalink = "/sale" });

        Assert.Equal(McpToolCallOutcome.Forbidden, inStoreB.Outcome);
        Assert.Equal(McpToolCallOutcome.Forbidden, copyOfB.Outcome);
        Assert.Equal(McpToolCallOutcome.Succeeded, inStoreA.Outcome);
    }

    [Fact]
    public async Task ALanguageVersion_RunsTheOperationsDryRunThenTheWrite()
    {
        _context.MultilanguagePageOperations
            .Setup(x => x.CreateLanguageAsync(It.IsAny<MultilanguagePageLanguageOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageLanguageOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(Preview("cultureName", null, "fr-FR"))
                : SiteBuilderOperationResult.Success(new GroupedSiteBuilderPage { Id = "fr-version", CultureName = "fr-FR" }));

        var result = await _context.CallAsync(Author(), Tool, new { kind = "language_version", multilanguage_page_id = "ml-a", language = "fr-FR" });

        var change = result.EnsureSucceeded().Read<SiteBuilderChange>();
        Assert.Equal(("fr-version", "ml-a"), (change.PageId, change.MultilanguagePageId));
        _context.MultilanguagePageOperations.Verify(x => x.CreateLanguageAsync(
            It.Is<MultilanguagePageLanguageOperationRequest>(r => r.MultilanguagePageId == "ml-a" && r.CultureName == "fr-FR" && r.CopyContent && !r.DryRun),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALanguageVersionCopiedFromASourceThatPlacesACustomSectionTypeTheThemeDoesNotHold_IsCreatedWithAWarningInItsLanguage(bool dryRun)
    {
        await SeedLanguageVersionsPlacingAMissingTypeAsync();
        _context.MultilanguagePageOperations
            .Setup(x => x.CreateLanguageAsync(It.IsAny<MultilanguagePageLanguageOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageLanguageOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(Preview("cultureName", null, "de-DE"))
                : SiteBuilderOperationResult.Success(new GroupedSiteBuilderPage { Id = "de-version", Name = "Root ml-a", CultureName = "de-DE" }));

        var change = (await _context.CallAsync(Author(), Tool, new { kind = "language_version", multilanguage_page_id = "ml-a", language = "de-DE", dryRun }))
            .EnsureSucceeded().Read<SiteBuilderChange>();

        var warning = Assert.Single(change.Warnings, x => x.Contains("(custom-stats)", StringComparison.Ordinal));
        Assert.StartsWith("de-DE: Page 'Root ml-a' places custom section types the theme does not hold (custom-stats): it can still be published, but the " +
            "storefront shows nothing in their place.", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyLanguageVersion_IsCreatedWithoutASectionTypeWarning()
    {
        await SeedLanguageVersionsPlacingAMissingTypeAsync();
        _context.MultilanguagePageOperations
            .Setup(x => x.CreateLanguageAsync(It.IsAny<MultilanguagePageLanguageOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(Preview("cultureName", null, "de-DE")));

        var change = (await _context.CallAsync(Author(), Tool,
            new { kind = "language_version", multilanguage_page_id = "ml-a", language = "de-DE", copy_content = false, dryRun = true })).EnsureSucceeded().Read<SiteBuilderChange>();

        Assert.DoesNotContain(change.Warnings, x => x.Contains("custom-stats", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACopyOfAMultilanguagePageThatPlacesACustomSectionTypeTheThemeDoesNotHold_IsCreatedWithAWarningPerLanguage(bool dryRun)
    {
        await SeedLanguageVersionsPlacingAMissingTypeAsync();
        _context.MultilanguagePageOperations
            .Setup(x => x.CloneAsync(It.IsAny<MultilanguagePageOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<MultilanguagePage>(Preview("name", null, "Root ml-a (copy)", "multilanguage-page"))
                : SiteBuilderOperationResult.Success(new MultilanguagePage { Id = "ml-copy", Name = "Root ml-a (copy)", StoreId = StoreA }));

        var change = (await _context.CallAsync(Author(), Tool, new { kind = "multilanguage_page_copy", multilanguage_page_id = "ml-a", dryRun }))
            .EnsureSucceeded().Read<SiteBuilderChange>();

        const string warning = "Page 'Root ml-a (copy)' places custom section types the theme does not hold (custom-stats)";
        Assert.Equal([$"en-US: {warning}", $"fr-FR: {warning}"],
            change.Warnings.Where(x => x.Contains("(custom-stats)", StringComparison.Ordinal)).Select(x => x[..(warning.Length + 7)]).Order(StringComparer.Ordinal));
    }

    private async Task SeedLanguageVersionsPlacingAMissingTypeAsync()
    {
        await _context.SeedThemeAsync(StoreA);
        const string content = """{ "settings": {}, "content": [ { "id": "s1", "type": "custom-stats" } ] }""";
        var english = _context.SeedDraft("ml-a-en", StoreA, content, multilanguagePageId: "ml-a");
        var french = _context.SeedDraft("ml-a-fr", StoreA, content, language: "fr-FR", multilanguagePageId: "ml-a");
        _context.MultilanguagePages
            .Setup(x => x.GetLanguagesAsync("ml-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MultilanguagePageLanguage { CultureName = "en-US", IsDefault = true, Page = english },
                new MultilanguagePageLanguage { CultureName = "fr-FR", Page = french },
                new MultilanguagePageLanguage { CultureName = "de-DE", Status = MultilanguagePageLanguage.Missing },
            ]);
    }

    [Fact]
    public async Task AMultilanguagePageOfAnotherStore_IsRefusedBeforeTheOperation()
    {
        var language = await _context.CallAsync(StoreAAuthor(), Tool, new { kind = "language_version", multilanguage_page_id = "ml-b", language = "fr-FR" });
        var copy = await _context.CallAsync(StoreAAuthor(), Tool, new { kind = "multilanguage_page_copy", multilanguage_page_id = "ml-b" });

        Assert.Equal(McpToolCallOutcome.Forbidden, language.Outcome);
        Assert.Equal(McpToolCallOutcome.Forbidden, copy.Outcome);
        _context.MultilanguagePageOperations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ADryRunOfACopyOfAMultilanguagePage_WritesNothing()
    {
        _context.MultilanguagePageOperations
            .Setup(x => x.CloneAsync(It.IsAny<MultilanguagePageOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.DryRun<MultilanguagePage>(Preview("name", null, "Root ml-a (copy)", "multilanguage-page")));

        var result = await _context.CallAsync(Author(), Tool, new { kind = "multilanguage_page_copy", multilanguage_page_id = "ml-a", dryRun = true });

        Assert.True(result.EnsureSucceeded().Read<SiteBuilderChange>().DryRun);
        _context.MultilanguagePageOperations.Verify(x => x.CloneAsync(It.Is<MultilanguagePageOperationRequest>(r => !r.DryRun),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
