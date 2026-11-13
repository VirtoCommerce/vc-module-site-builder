using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.MCP;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

// The restore of database pages and theme files runs through the module's own history operation and designer, over
// the page store and the theme of the test; the versions are what History answers.
public sealed class RestoreVersionToolTests : IAsyncDisposable
{
    private const string Tool = "restore_version";
    private const string VersionId = "aaaaaaaaaaaaaaaaaaaaaaaaaa1";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string FilePagePath = "landing/spring.page";
    private const string FilePageId = "pages/landing/spring.page";

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;

    public RestoreVersionToolTests()
    {
        _pages = _context.UsePageStore();
        _context.SeedPage("a1", StoreA, published: true, draft: true);
        _context.SeedPage("b1", StoreB, published: true, draft: true);
        _context.History
            .Setup(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<HistoryItem>(), VersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, HistoryItem item, string _, CancellationToken _) => item.Type == HistoryItemType.Page ? SampleContent : ProductTemplate);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Editor() => McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update);

    private static McpTestUser StoreAEditor() =>
        McpTestUser.Create("store-a-editor").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update);

    [Fact]
    public void TheTool_NeedsBuilderReadAndUpdate()
    {
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), Editor()));
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), StoreAEditor()));
        Assert.False(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), Reader()));
    }

    [Fact]
    public async Task ADryRun_ShowsWhatTheDraftLoses_AndItsVersion_AndRestoresNothing()
    {
        var restore = await DryRunAsync("a1");

        Assert.True(restore.DryRun);
        Assert.Contains(restore.Changes, x => x.Field == "section hero-banner 'hero': title" && x.From?.ToString() == "Summer" && x.To?.ToString() == "Spring");
        Assert.Equal(await DraftVersionAsync("a1"), restore.ExpectedVersions["page:a1"]);
        Assert.Contains("Summer", await DraftContentAsync("a1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfirmedRestore_WithTheVersionRead_RestoresTheDraft()
    {
        var expected = (await DryRunAsync("a1")).ExpectedVersions;

        var result = await _context.CallAsync(Editor(), Tool, new { store_id = StoreA, version_id = VersionId, page_id = "a1", expected_versions = expected });

        Assert.True(result.AskedForConfirmation);
        Assert.False(result.EnsureSucceeded().Read<VersionRestore>().DryRun);
        Assert.Contains("Spring", await DraftContentAsync("a1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVersionTokenOfTheReadTool_IsTheExpectedVersion()
    {
        var token = (await _context.CallAsync(Editor(), "search_site_pages", new { page_id = "a1" })).EnsureSucceeded().Read<SitePageDetails>().VersionToken;

        var result = await _context.CallAsync(Editor(), Tool,
            new { store_id = StoreA, version_id = VersionId, page_id = "a1", expected_versions = new Dictionary<string, string> { ["page:a1"] = token } });

        result.EnsureSucceeded();
        Assert.Contains("Spring", await DraftContentAsync("a1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclinedRestore_RestoresNothing()
    {
        var expected = (await DryRunAsync("a1")).ExpectedVersions;

        var result = await _context.CallAsync(Editor(), Tool, new { store_id = StoreA, version_id = VersionId, page_id = "a1", expected_versions = expected },
            new McpToolCallOptions { DeclineConfirmation = true });

        Assert.Equal(McpToolCallOutcome.Declined, result.Outcome);
        Assert.Contains("Summer", await DraftContentAsync("a1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestoreWithoutTheVersions_IsRefused()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { store_id = StoreA, version_id = VersionId, page_id = "a1" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("expected_versions", result.Error, StringComparison.Ordinal);
        Assert.Contains("Summer", await DraftContentAsync("a1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADraftSavedSinceItsVersionWasRead_IsNotOverwritten()
    {
        var expected = (await DryRunAsync("a1")).ExpectedVersions;
        var token = await DraftVersionAsync("a1");
        await _context.CallAsync(Editor(), "edit_page_draft", new
        {
            page_id = "a1",
            version_token = token,
            operations = new[] { new { op = "set_section_settings", section_id = "hero", settings = new { title = "Autumn" } } },
        });

        var result = await _context.CallAsync(Editor(), Tool, new { store_id = StoreA, version_id = VersionId, page_id = "a1", expected_versions = expected });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since", result.Error, StringComparison.Ordinal);
        Assert.Contains("Autumn", await DraftContentAsync("a1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageOfAnotherStore_IsForbidden()
    {
        var result = await _context.CallAsync(StoreAEditor(), Tool,
            new { store_id = StoreB, version_id = VersionId, page_id = "b1", expected_versions = new Dictionary<string, string>(), dryRun = true });
        var named = await _context.CallAsync(StoreAEditor(), Tool,
            new { store_id = StoreA, version_id = VersionId, page_id = "b1", dryRun = true });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Equal(McpToolCallOutcome.Forbidden, named.Outcome);
        Assert.Contains("Summer", await DraftContentAsync("b1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThemeFile_NeedsThePermissionOfItsPart()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "templates/product.json", ProductTemplate.Replace("\"reviews\"", "\"reviews2\"", StringComparison.Ordinal), draft: true);
        var templates = McpTestUser.Create("templates").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Templates);
        var arguments = new { store_id = StoreA, version_id = VersionId, theme_files = new[] { "default/templates/product.json" }, dryRun = true };

        var refused = await _context.CallAsync(Editor(), Tool, arguments);
        var preview = (await _context.CallAsync(templates, Tool, arguments)).EnsureSucceeded().Read<VersionRestore>();
        var restored = await _context.CallAsync(templates, Tool,
            new { store_id = StoreA, version_id = VersionId, theme_files = new[] { "default/templates/product.json" }, expected_versions = preview.ExpectedVersions });

        Assert.Equal(McpToolCallOutcome.Forbidden, refused.Outcome);
        Assert.Contains("builder:templates", refused.Error, StringComparison.Ordinal);
        Assert.Equal(["theme:default/templates/product.json"], preview.ExpectedVersions.Keys);
        restored.EnsureSucceeded();
        Assert.Equal(ProductTemplate, _context.ReadThemeFile(StoreA, "templates/product.json", draft: true));
    }

    [Fact]
    public async Task AFilePage_IsRestoredFromItsCommit_WhenItsContentIsTheOneRead()
    {
        const string current = """{ "settings": {}, "content": [ { "id": "hero", "type": "hero-banner", "title": "Summer" } ] }""";
        _context.FilePageOperations
            .Setup(x => x.RestoreVersionAsync(It.IsAny<FilePageRestoreVersionOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilePageRestoreVersionOperationRequest request, ClaimsPrincipal _, CancellationToken _) =>
            {
                if (!request.DryRun)
                {
                    return SiteBuilderOperationResult.Success(new FilePageVersionRestore { CommitSha = "new-commit", RestoredFrom = request.Sha });
                }

                var preview = new SiteBuilderChangePreview();
                preview.AddObject("file-page", request.Path).AddField("content", current, SampleContent);
                return SiteBuilderOperationResult.DryRun<FilePageVersionRestore>(preview);
            });
        var arguments = new { store_id = StoreA, version_id = Commit, page_id = FilePageId };

        var dryRun = (await _context.CallAsync(Editor(), Tool, new { arguments.store_id, arguments.version_id, arguments.page_id, dryRun = true }))
            .EnsureSucceeded().Read<VersionRestore>();
        var stale = await _context.CallAsync(Editor(), Tool,
            new { arguments.store_id, arguments.version_id, arguments.page_id, expected_versions = new Dictionary<string, string> { [$"page:{FilePageId}"] = "stale" } });
        Verify(Times.Never());
        var restored = (await _context.CallAsync(Editor(), Tool,
            new { arguments.store_id, arguments.version_id, arguments.page_id, expected_versions = dryRun.ExpectedVersions })).EnsureSucceeded().Read<VersionRestore>();

        Assert.Equal(FilePages.VersionOf(current), dryRun.ExpectedVersions[$"page:{FilePageId}"]);
        Assert.Contains(dryRun.Changes, x => x.Field == "section hero-banner 'hero': title" && x.To?.ToString() == "Spring");
        Assert.Equal(McpToolCallOutcome.Failed, stale.Outcome);
        Assert.Equal("new-commit", restored.Commit);
        Verify(Times.Once());

        void Verify(Times times) => _context.FilePageOperations.Verify(x => x.RestoreVersionAsync(
            It.Is<FilePageRestoreVersionOperationRequest>(r => !r.DryRun && r.Path == FilePagePath && r.Sha == Commit), It.IsAny<ClaimsPrincipal>(),
            It.IsAny<CancellationToken>()), times);
    }

    [Fact]
    public async Task AFilePageOfAnotherStore_IsForbidden()
    {
        var result = await _context.CallAsync(StoreAEditor(), Tool, new { store_id = StoreB, version_id = Commit, page_id = FilePageId, dryRun = true });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        _context.FilePageOperations.VerifyNoOtherCalls();
    }

    private async Task<VersionRestore> DryRunAsync(string pageId) =>
        (await _context.CallAsync(Editor(), Tool, new { store_id = StoreA, version_id = VersionId, page_id = pageId, dryRun = true }))
        .EnsureSucceeded().Read<VersionRestore>();

    private async Task<SiteBuilderContentSnapshot> DraftAsync(string pageId) =>
        await _pages.LoadGroupContentAsync(await _pages.GetByIdAsync(pageId), cancellationToken: CancellationToken.None);

    private async Task<string> DraftContentAsync(string pageId) => (await DraftAsync(pageId)).Content;

    private async Task<string> DraftVersionAsync(string pageId) => (await DraftAsync(pageId)).ETag;
}
