using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

// Pages stored as files are listed by the module's own file search over the blob storage of the test, and their versions
// come from the git history the content query service reads; the database pages are what the pages list answers.
public sealed class FilePageListAndHistoryToolTests : IAsyncDisposable
{
    private const string SearchTool = "search_site_pages";
    private const string VersionsTool = "list_versions";
    private const string PageId = "pages/about.page";

    private readonly McpTestContext _context = new();
    private readonly List<GroupedSiteBuilderPage> _groups = [Group("a1", StoreA), Group("a2", StoreA), Group("b1", StoreB)];
    private GitHistoryQuery _lastHistoryQuery;

    public FilePageListAndHistoryToolTests()
    {
        _context.AddGroups([.. _groups]);
        _context.ListPages(() => _groups.Select(LandingRow));
        SeedFile("pages", StoreA, "about.page", "About us", "/about");
        SeedFile("pages", StoreA, "contacts.page", "Contacts", "/contacts");
        SeedFile("pages", StoreA, "landing/spring.page", "Spring sale", "/spring");
        SeedFile("blogs", StoreA, "launch.page", "Launch", "/blog/launch");
        SeedFile("pages", StoreB, "secret.page", "Secret", "/secret");
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public async Task TheFilePagesOfAStore_FollowItsDatabasePages_MarkedAsFiles()
    {
        var page = await SearchAsync(Reader(), new { store_id = StoreA });

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(["a1", "a2", PageId, "pages/contacts.page", "blogs/launch.page"], page.Items.Select(x => x.Id));
        var file = page.Items[2];
        Assert.Equal(("File", "About us", "/about", StoreA), (file.Type, file.Name, file.Permalink, file.StoreId));
        Assert.Null(file.Status);
    }

    [Fact]
    public async Task AStoreReader_ListsTheFilesOfTheirStoreOnly()
    {
        var own = await SearchAsync(StoreAReader(), new { store_id = StoreA, page_type = "file" });
        var other = await _context.CallAsync(StoreAReader(), SearchTool, new { store_id = StoreB, page_type = "File" });

        Assert.DoesNotContain(own.Items, x => x.Id == "pages/secret.page");
        Assert.All(own.Items, x => Assert.Equal(StoreA, x.StoreId));
        Assert.Equal(McpToolCallOutcome.Forbidden, other.Outcome);
    }

    [Fact]
    public async Task WithoutAStore_OnlyDatabasePagesAreListed_AndFilesAreAskedForPerStore()
    {
        var page = await SearchAsync(Reader(), new { });
        var files = await _context.CallAsync(Reader(), SearchTool, new { page_type = "File" });

        Assert.All(page.Items, x => Assert.NotEqual("File", x.Type));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(McpToolCallOutcome.Failed, files.Outcome);
        Assert.Contains("store_id", files.Error);
    }

    [Fact]
    public async Task AFilterOnlyDatabasePagesHave_LeavesTheFilesOut_OrIsRefusedForThem()
    {
        var page = await SearchAsync(Reader(), new { store_id = StoreA, status = "Published" });
        var files = await _context.CallAsync(Reader(), SearchTool, new { store_id = StoreA, page_type = "File", status = "Published" });

        Assert.Equal(["a1", "a2"], page.Items.Select(x => x.Id));
        Assert.Equal(McpToolCallOutcome.Failed, files.Outcome);
    }

    [Fact]
    public async Task TwoPages_HoldEveryDatabaseAndFilePageOnce()
    {
        var first = await SearchAsync(Reader(), new { store_id = StoreA, take = 3 });
        var second = await SearchAsync(Reader(), new { store_id = StoreA, skip = first.NextSkip, take = 3 });

        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(["a1", "a2", PageId, "pages/contacts.page", "blogs/launch.page"], first.Items.Concat(second.Items).Select(x => x.Id));
    }

    [Fact]
    public async Task AFolder_ListsItsFilesOnly_AndTheKeywordNarrowsThem()
    {
        var landing = await SearchAsync(Reader(), new { store_id = StoreA, folder = "pages/landing" });
        var found = await SearchAsync(Reader(), new { store_id = StoreA, page_type = "File", keyword = "contact" });
        var other = await _context.CallAsync(Reader(), SearchTool, new { store_id = StoreA, folder = "themes" });

        Assert.Equal(["pages/landing/spring.page"], landing.Items.Select(x => x.Id));
        Assert.Equal(["pages/contacts.page"], found.Items.Select(x => x.Id));
        Assert.Equal(McpToolCallOutcome.Failed, other.Outcome);
    }

    [Fact]
    public async Task OnGit_TheVersionsOfAFilePage_AreItsCommits_UnpublishedFirst()
    {
        UseGitHistory(Versions(("c3", false, "editor"), ("c2", false, "kate"), ("c1", true, "anna")));

        var answer = await ListVersionsAsync(Reader("editor"), new { store_id = StoreA, page_id = PageId });

        Assert.True(answer.HistoryAvailable);
        Assert.Equal(["c3", "c2", "c1"], answer.Versions.Select(x => x.VersionId));
        Assert.Equal([true, false, false], answer.Versions.Select(x => x.Mine == true));
        Assert.Equal([false, false, true], answer.Versions.Select(x => x.Published));
        Assert.All(answer.Versions, x => Assert.Equal([$"page:{PageId}"], x.Items));
        Assert.Null(answer.NextCursor);
    }

    [Fact]
    public async Task OnGit_TwoPagesOfVersions_HoldEveryCommitOnce_AndTheNextBranchesBringOnlyUnpublishedOnes()
    {
        UseGitHistory(after => after == null
            ? MoreBranches(Versions(("c3", false, "kate"), ("c2", true, "anna"), ("c1", true, "anna")), "Y3Vyc29y")
            : Versions(("c4", false, "lee"), ("c2", true, "anna")));

        var first = await ListVersionsAsync(Reader(), new { store_id = StoreA, page_id = PageId, take = 2 });
        var second = await ListVersionsAsync(Reader(), new { store_id = StoreA, page_id = PageId, take = 2, cursor = first.NextCursor });
        var third = await ListVersionsAsync(Reader(), new { store_id = StoreA, page_id = PageId, take = 2, cursor = second.NextCursor });

        Assert.Equal(["c3", "c2"], first.Versions.Select(x => x.VersionId));
        Assert.Equal(["c1"], second.Versions.Select(x => x.VersionId));
        Assert.Contains("more branches", second.Note);
        Assert.Equal(["c4"], third.Versions.Select(x => x.VersionId));
        Assert.Equal("Y3Vyc29y", _lastHistoryQuery.After);
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task OnBlobStorage_AFilePageHasNoVersions_AndTheAnswerSaysWhy()
    {
        var answer = await ListVersionsAsync(Reader(), new { store_id = StoreA, page_id = PageId });

        Assert.False(answer.HistoryAvailable);
        Assert.Contains("blob storage", answer.Note);
        Assert.Empty(answer.Versions);
        _context.GitHistory.Verify(x => x.GetHistoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GitHistoryQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TheVersionsOfAFilePageOfAnotherStore_AreForbidden()
    {
        UseGitHistory(Versions(("c1", true, "anna")));

        var result = await _context.CallAsync(StoreAReader(), VersionsTool, new { store_id = StoreB, page_id = "pages/secret.page" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        _context.GitHistory.Verify(x => x.GetHistoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GitHistoryQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AFilePageAndThemeFiles_AreListedApart()
    {
        var result = await _context.CallAsync(Reader(), VersionsTool, new { store_id = StoreA, page_id = PageId, theme_files = new[] { "default/templates/product.json" } });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ACommitOfTheListing_IsWhatRestoreVersionRestores()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        UseGitHistory(Versions((sha, true, "anna")));
        _context.FilePageOperations
            .Setup(x => x.RestoreVersionAsync(It.IsAny<FilePageRestoreVersionOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilePageRestoreVersionOperationRequest request, ClaimsPrincipal _, CancellationToken _) =>
            {
                if (!request.DryRun)
                {
                    return SiteBuilderOperationResult.Success(new FilePageVersionRestore { CommitSha = "new-commit", RestoredFrom = request.Sha });
                }

                var preview = new SiteBuilderChangePreview();
                preview.AddObject("file-page", request.Path).AddField("content", SampleContent, SampleContent);
                return SiteBuilderOperationResult.DryRun<FilePageVersionRestore>(preview);
            });
        var editor = McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update);

        var version = (await ListVersionsAsync(editor, new { store_id = StoreA, page_id = PageId })).Versions.Single().VersionId;
        var dryRun = (await _context.CallAsync(editor, "restore_version", new { store_id = StoreA, version_id = version, page_id = PageId, dryRun = true }))
            .EnsureSucceeded().Read<VersionRestore>();
        var restored = (await _context.CallAsync(editor, "restore_version",
            new { store_id = StoreA, version_id = version, page_id = PageId, expected_versions = dryRun.ExpectedVersions })).EnsureSucceeded().Read<VersionRestore>();

        Assert.Equal("new-commit", restored.Commit);
        _context.FilePageOperations.Verify(x => x.RestoreVersionAsync(
            It.Is<FilePageRestoreVersionOperationRequest>(r => !r.DryRun && r.Path == "about.page" && r.Sha == sha), It.IsAny<ClaimsPrincipal>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private void SeedFile(string contentType, string storeId, string path, string name, string permalink) =>
        _context.Blobs.Files[$"{contentType}/{storeId}/{path}"] = $$"""{ "settings": { "name": "{{name}}", "permalink": "{{permalink}}" }, "content": [] }""";

    private void UseGitHistory(GitPageHistory history) => UseGitHistory(_ => history);

    // The history of the page's file by the branch cursor it is asked with; the user's own draft branch is the one
    // named after them.
    private void UseGitHistory(Func<string, GitPageHistory> history)
    {
        _context.GitFlow = true;
        _context.GitHistory
            .Setup(x => x.GetHistoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GitHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, GitHistoryQuery query, CancellationToken _) =>
            {
                _lastHistoryQuery = query;
                return history(query.After);
            });
    }

    private static GitPageHistory Versions(params (string Sha, bool Published, string Author)[] versions) => new()
    {
        Versions = versions.Select(x => new GitPageVersion
        {
            Sha = x.Sha,
            Message = $"Saved by {x.Author}",
            AuthorName = x.Author,
            Published = x.Published,
            Branches = x.Published ? ["main"] : [new GitContentOptions().GetPageLocation("o/r", "pages", "about.page", x.Author).Branch],
        }).ToList(),
    };

    private static GitPageHistory MoreBranches(GitPageHistory history, string endCursor) =>
        new() { Versions = history.Versions, Truncated = true, EndCursor = endCursor };

    private async Task<McpPage<SitePageRow>> SearchAsync(McpTestUser user, object arguments) =>
        (await _context.CallAsync(user, SearchTool, arguments)).EnsureSucceeded().Read<McpPage<SitePageRow>>();

    private async Task<ContentVersionsAnswer> ListVersionsAsync(McpTestUser user, object arguments) =>
        (await _context.CallAsync(user, VersionsTool, arguments)).EnsureSucceeded().Read<ContentVersionsAnswer>();
}
