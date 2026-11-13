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
using VirtoCommerce.SiteBuilder.MCP;
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

    internal static SiteBuilderChangePreview Preview(string field, string from, string to, string objectType = "page", string objectId = null,
        bool affectsPublished = false) => new()
        {
            AffectsPublished = affectsPublished,
            Objects =
            [
                new SiteBuilderObjectChange
                {
                    ObjectType = objectType,
                    ObjectId = objectId,
                    Fields = [new SiteBuilderFieldChange { Name = field, From = from, To = to }],
                },
            ],
        };
}
