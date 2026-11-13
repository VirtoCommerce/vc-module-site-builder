using System;
using System.Linq;
using System.Security.Claims;
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

public sealed class PublishPageToolTests : IAsyncDisposable
{
    private static readonly McpToolCallOptions Approved = new() { AsApprovedChange = true };

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;

    public PublishPageToolTests()
    {
        _pages = _context.UsePageStore();
        _context.SeedPage("new", StoreA, published: false, draft: true);
        _context.SeedPage("live", StoreA, published: true, draft: false);
        _context.SeedPage("edited", StoreA, published: true, draft: true);
        _context.SeedPage("b1", StoreB, published: false, draft: true);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Publisher() => McpTestUser.Create("publisher").WithPermissions(Permissions.Read, Permissions.Publish);

    private static McpTestUser StoreAPublisher() =>
        McpTestUser.Create("store-a-publisher").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Publish);

    [Theory]
    [InlineData("publish_page")]
    [InlineData("unpublish_page")]
    public void TheTools_NeedBuilderReadAndPublish(string tool)
    {
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(tool), Publisher()));
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(tool), StoreAPublisher()));
        Assert.False(_context.Harness.IsVisible(_context.Harness.GetTool(tool), McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update)));
    }

    [Fact]
    public async Task APublish_AlwaysWaitsForApproval_AndPublishesNothing()
    {
        var result = await _context.CallAsync(Publisher(), "publish_page", new { page_id = "new" });

        Assert.Equal(McpToolCallOutcome.PendingApproval, result.Outcome);
        Assert.Contains(result.Read<SiteBuilderChange>().Changes, x => x.Field == "status" && x.From?.ToString() == "Draft" && x.To?.ToString() == "Published");
        Assert.Equal("Draft", (await _pages.GetByIdAsync("new")).Status);
    }

    [Fact]
    public async Task AnApprovedPublish_PublishesTheDraft()
    {
        var result = await _context.CallAsync(Publisher(), "publish_page", new { page_id = "edited" }, Approved);

        Assert.True(result.EnsureSucceeded().Read<SiteBuilderChange>().AffectsPublished);
        Assert.Equal(["Published"], (await _pages.GetByIdAsync("edited")).Pages.Select(x => x.Status));
        Assert.Single(_context.SavedVersions.Records);
    }

    [Fact]
    public async Task AnUnpublish_WaitsForApproval_AndIsRefusedWhileTheDraftExists()
    {
        var held = await _context.CallAsync(Publisher(), "unpublish_page", new { page_id = "live" });
        var refused = await _context.CallAsync(Publisher(), "unpublish_page", new { page_id = "edited" }, Approved);

        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.Equal("Published", (await _pages.GetByIdAsync("live")).Status);
        Assert.Equal(McpToolCallOutcome.Failed, refused.Outcome);
        Assert.Contains("discard_draft", refused.Error);
    }

    [Fact]
    public async Task APageOfAnotherStore_IsForbidden()
    {
        var result = await _context.CallAsync(StoreAPublisher(), "publish_page", new { page_id = "b1" }, Approved);

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("b1")).Status);
    }

    [Theory]
    [InlineData("publish_page")]
    [InlineData("unpublish_page")]
    public async Task APageStoredAsAFile_IsNotAvailableYet(string tool)
    {
        var result = await _context.CallAsync(Publisher(), tool, new { page_id = "pages/about.md" }, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("not available for pages stored as files yet", result.Error);
    }

    [Fact]
    public async Task ChosenLanguages_ArePublishedSourceLanguageFirst()
    {
        var english = _context.SeedPage("ml-en", StoreA, published: false, draft: true);
        var french = _context.SeedPage("ml-fr", StoreA, published: false, draft: true);
        french.CultureName = "fr-FR";
        _context.AddMultilanguagePages(Root("ml", StoreA));
        _context.MultilanguagePages
            .Setup(x => x.GetLanguagesAsync("ml", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MultilanguagePageLanguage { CultureName = "en-US", IsDefault = true, Page = english },
                new MultilanguagePageLanguage { CultureName = "fr-FR", Page = french },
                new MultilanguagePageLanguage { CultureName = "de-DE", Status = MultilanguagePageLanguage.Missing },
            ]);

        var missing = await _context.CallAsync(Publisher(), "publish_page", new { multilanguage_page_id = "ml", languages = new[] { "de-DE" } }, Approved);
        var result = await _context.CallAsync(Publisher(), "publish_page", new { multilanguage_page_id = "ml", languages = new[] { "fr-FR" } }, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, missing.Outcome);
        Assert.False(result.EnsureSucceeded().Read<SiteBuilderChange>().DryRun);
        Assert.Equal("Published", (await _pages.GetByIdAsync("ml-fr")).Status);
        Assert.Equal("Draft", (await _pages.GetByIdAsync("ml-en")).Status);
    }

    [Fact]
    public async Task AllLanguages_RunsPublishAll_AndReportsTheSkippedOnes()
    {
        _context.AddMultilanguagePages(Root("ml", StoreA));
        _context.MultilanguagePageOperations
            .Setup(x => x.PublishAllAsync(It.IsAny<MultilanguagePageOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<MultilanguagePageBatchResult>(CreatePageToolTests.Preview("status", "Draft", "Published", objectId: "ml-en", affectsPublished: true))
                : SiteBuilderOperationResult.Success(new MultilanguagePageBatchResult
                {
                    ProcessedCultureNames = ["en-US"],
                    Skipped = [new MultilanguagePageBatchSkip { CultureName = "fr-FR", Reason = MultilanguagePageBatchResult.NoDraftReason }],
                }, affectsPublished: true));

        var held = await _context.CallAsync(Publisher(), "publish_page", new { multilanguage_page_id = "ml", all_languages = true });
        var result = await _context.CallAsync(Publisher(), "publish_page", new { multilanguage_page_id = "ml", all_languages = true }, Approved);

        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.Contains(result.EnsureSucceeded().Read<SiteBuilderChange>().Warnings, x => x.StartsWith("fr-FR: no draft", StringComparison.Ordinal));
    }
}
