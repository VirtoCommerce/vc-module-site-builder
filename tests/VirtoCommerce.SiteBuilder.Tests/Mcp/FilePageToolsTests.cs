using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.MCP;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

// Pages stored as files: read and edited through the designer's own read and save over the blob storage of the test,
// published, unpublished and discarded through the file page operations, whose git work is their own tests' business.
public sealed class FilePageToolsTests : IAsyncDisposable
{
    private const string Path = "landing/spring.page";
    private const string PageId = "pages/landing/spring.page";
    private const string Published = """{ "settings": { "type": "settings", "name": "Spring" }, "content": [ { "id": "hero", "type": "hero-banner", "title": "Spring" } ] }""";
    private static readonly McpToolCallOptions Approved = new() { AsApprovedChange = true };

    private readonly McpTestContext _context = new();

    public FilePageToolsTests()
    {
        _context.SeedFilePage(StoreA, Path, Published);
        _context.SeedFilePage(StoreB, Path, Published);
        _context.PublishStatuses
            .Setup(x => x.GetFilePageStatusAsync(It.IsAny<SiteBuilderFilePageQueryRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.Success(new PagePublishStatus { Published = true, Flow = "git" }));
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Editor() => McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update);

    private static McpTestUser StoreAEditor() =>
        McpTestUser.Create("store-a-editor").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update, Permissions.Publish);

    private static McpTestUser Publisher() => McpTestUser.Create("publisher").WithPermissions(Permissions.Read, Permissions.Publish);

    [Fact]
    public async Task TheReadTool_ReadsAFilePage_WithItsSectionsAndVersion()
    {
        var page = await ReadAsync(Editor(), StoreA);

        Assert.Equal(PageId, page.Id);
        Assert.Equal("draft", page.ContentVersion);
        Assert.Equal("git", page.Flow);
        Assert.Equal("hero", page.Sections[0]!["id"]!.ToString());
        Assert.Equal(FilePages.VersionOf(Published), page.VersionToken);
    }

    [Fact]
    public async Task AFilePage_NeedsItsStore()
    {
        var result = await _context.CallAsync(Editor(), "search_site_pages", new { page_id = PageId });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("store_id", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilePageOfAnotherStore_IsForbidden_ToReadAndToEdit()
    {
        var read = await _context.CallAsync(StoreAEditor(), "search_site_pages", new { page_id = PageId, store_id = StoreB });
        var edit = await _context.CallAsync(StoreAEditor(), "edit_page_draft",
            new { page_id = PageId, store_id = StoreB, version_token = FilePages.VersionOf(Published), operations = new[] { SetTitle("Summer") } });

        Assert.Equal(McpToolCallOutcome.Forbidden, read.Outcome);
        Assert.Equal(McpToolCallOutcome.Forbidden, edit.Outcome);
        Assert.Null(_context.ReadFilePage(StoreB, Path, draft: true));
    }

    [Fact]
    public async Task AnEdit_SavesTheDraft_LeavesThePublishedPage_AndAnswersTheNextVersion()
    {
        var edit = (await _context.CallAsync(Editor(), "edit_page_draft",
            new { page_id = PageId, store_id = StoreA, version_token = FilePages.VersionOf(Published), operations = new[] { SetTitle("Summer") } }))
            .EnsureSucceeded().Read<PageDraftEdit>();

        var draft = _context.ReadFilePage(StoreA, Path, draft: true);
        Assert.False(edit.DryRun);
        Assert.Contains("Summer", draft, StringComparison.Ordinal);
        Assert.Equal(Published, _context.ReadFilePage(StoreA, Path));
        Assert.Equal(FilePages.VersionOf(draft), edit.VersionToken);
        Assert.Contains(edit.Changes, x => x.Field == "section hero-banner 'hero': title" && x.To?.ToString() == "Summer");
        Assert.Equal(edit.VersionToken, (await ReadAsync(Editor(), StoreA)).VersionToken);
    }

    [Fact]
    public async Task ADryRunEdit_WritesNothing()
    {
        var edit = (await _context.CallAsync(Editor(), "edit_page_draft",
            new { page_id = PageId, store_id = StoreA, version_token = FilePages.VersionOf(Published), operations = new[] { SetTitle("Summer") }, dryRun = true }))
            .EnsureSucceeded().Read<PageDraftEdit>();

        Assert.True(edit.DryRun);
        Assert.NotEmpty(edit.Changes);
        Assert.Null(_context.ReadFilePage(StoreA, Path, draft: true));
    }

    [Fact]
    public async Task AnEditOfContentThatChanged_IsRefused()
    {
        _context.SeedFilePage(StoreA, Path, Published.Replace("Spring", "Winter", StringComparison.Ordinal), draft: true);

        var result = await _context.CallAsync(Editor(), "edit_page_draft",
            new { page_id = PageId, store_id = StoreA, version_token = FilePages.VersionOf(Published), operations = new[] { SetTitle("Summer") } });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("changed since you read it", result.Error, StringComparison.Ordinal);
        Assert.Contains("Winter", _context.ReadFilePage(StoreA, Path, draft: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilePage_HoldsNoSharedComponent()
    {
        var result = await _context.CallAsync(Editor(), "edit_page_draft", new
        {
            page_id = PageId,
            store_id = StoreA,
            version_token = FilePages.VersionOf(Published),
            operations = new[] { new { op = "place_shared_component", shared_component_id = "sc1" } },
        });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("database pages only", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublish_WaitsForApproval_AndPublishesNothing()
    {
        SetUpPublish(ConflictShipment());

        var result = await _context.CallAsync(Publisher(), "publish_page", new { page_id = PageId, store_id = StoreA });

        Assert.Equal(McpToolCallOutcome.PendingApproval, result.Outcome);
        Assert.Contains(result.Read<SiteBuilderChange>().Changes, x => x.Field == "status" && x.To?.ToString() == "Published");
        VerifyPublished(Times.Never());
    }

    [Fact]
    public async Task AConflictingPublish_IsReportedWithTheWaysOut_AndNotRetried()
    {
        SetUpPublish(ConflictShipment());

        var change = (await _context.CallAsync(Publisher(), "publish_page", new { page_id = PageId, store_id = StoreA }, Approved))
            .EnsureSucceeded().Read<SiteBuilderChange>();

        Assert.False(change.DryRun);
        Assert.Equal("conflict", change.Shipment.State);
        Assert.True(change.Shipment.CanRebase);
        Assert.Equal(7, change.Shipment.PullRequest);
        Assert.Contains(change.Warnings, x => x.Contains("rebase: true", StringComparison.Ordinal) && x.Contains("new approval", StringComparison.Ordinal));
        VerifyPublished(Times.Once());
        _context.FilePageOperations.Verify(x => x.PublishAsync(It.Is<FilePagePublishOperationRequest>(r => r.Rebase), It.IsAny<ClaimsPrincipal>(),
            It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task ARebase_IsACallOfItsOwn_ThatWaitsForApproval()
    {
        SetUpPublish(new FilePageShipment { GitFlow = true, State = GitPublishState.Merged, Merge = new GitPublishResult { State = GitPublishState.Merged } });

        var held = await _context.CallAsync(Publisher(), "publish_page", new { page_id = PageId, store_id = StoreA, rebase = true });
        VerifyPublished(Times.Never());

        var approved = (await _context.CallAsync(Publisher(), "publish_page", new { page_id = PageId, store_id = StoreA, rebase = true }, Approved))
            .EnsureSucceeded().Read<SiteBuilderChange>();

        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.Equal("merged", approved.Shipment.State);
        _context.FilePageOperations.Verify(x => x.PublishAsync(It.Is<FilePagePublishOperationRequest>(r => r.Rebase && !r.DryRun && r.Path == Path),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RebaseOfADatabasePage_IsRefused()
    {
        var result = await _context.CallAsync(Publisher(), "publish_page", new { page_id = "a1", rebase = true }, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("rebase goes with a page stored as a file", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilePageOfAnotherStore_IsNotPublished()
    {
        SetUpPublish(ConflictShipment());

        var result = await _context.CallAsync(StoreAEditor(), "publish_page", new { page_id = PageId, store_id = StoreB }, Approved);

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        VerifyPublished(Times.Never());
    }

    [Fact]
    public async Task AnApprovedUnpublish_RunsTheOperation()
    {
        _context.FilePageOperations
            .Setup(x => x.UnpublishAsync(It.IsAny<FilePageOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilePageOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<FilePageShipment>(Preview("status", "Published", "Draft"))
                : SiteBuilderOperationResult.Success(new FilePageShipment { State = GitPublishState.Merged }, affectsPublished: true));

        var held = await _context.CallAsync(Publisher(), "unpublish_page", new { page_id = PageId, store_id = StoreA });
        var change = (await _context.CallAsync(Publisher(), "unpublish_page", new { page_id = PageId, store_id = StoreA }, Approved))
            .EnsureSucceeded().Read<SiteBuilderChange>();

        Assert.Equal(McpToolCallOutcome.PendingApproval, held.Outcome);
        Assert.Equal("blob", change.Shipment.Flow);
        _context.FilePageOperations.Verify(x => x.UnpublishAsync(It.Is<FilePageOperationRequest>(r => !r.DryRun), It.IsAny<ClaimsPrincipal>(),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ADiscard_IsConfirmed_AndADeclinedOneDiscardsNothing()
    {
        SetUpBlobDiscard(DraftDiscardStatus.Discarded);

        var declined = await _context.CallAsync(Editor(), "discard_draft", new { kind = "page", page_id = PageId, store_id = StoreA },
            new McpToolCallOptions { DeclineConfirmation = true });
        VerifyDiscarded(Times.Never());

        var result = await _context.CallAsync(Editor(), "discard_draft", new { kind = "page", page_id = PageId, store_id = StoreA });

        Assert.Equal(McpToolCallOutcome.Declined, declined.Outcome);
        Assert.True(result.AskedForConfirmation);
        Assert.Contains(result.EnsureSucceeded().Read<SiteBuilderChange>().Changes, x => x.To?.ToString() == "Spring");
        VerifyDiscarded(Times.Once());
    }

    [Fact]
    public async Task ADraftThatIsTheOnlyCopy_IsNotDiscarded()
    {
        SetUpBlobDiscard(DraftDiscardStatus.OnlyCopy);

        var result = await _context.CallAsync(Editor(), "discard_draft", new { kind = "page", page_id = PageId, store_id = StoreA });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("never published", result.Error, StringComparison.Ordinal);
        VerifyDiscarded(Times.Never());
    }

    [Fact]
    public async Task InGit_TheDraftBranchIsDiscarded_ButNotAPageNeverPublished()
    {
        _context.FilePageOperations
            .Setup(x => x.DiscardDraftAsync(It.IsAny<FileDraftDiscardOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.Invalid(new FileDraftDiscard { KeptInGit = true }, "in git"));
        var published = Published;
        _context.FilePageOperations
            .Setup(x => x.DiscardGitDraftAsync(It.IsAny<FilePageOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilePageOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun(Preview("content", Published.Replace("Spring", "Summer", StringComparison.Ordinal), published))
                : SiteBuilderOperationResult.Success());

        var discarded = await _context.CallAsync(Editor(), "discard_draft", new { kind = "page", page_id = PageId, store_id = StoreA });
        published = null;
        var onlyCopy = await _context.CallAsync(Editor(), "discard_draft", new { kind = "page", page_id = PageId, store_id = StoreA });

        Assert.False(discarded.EnsureSucceeded().Read<SiteBuilderChange>().DryRun);
        Assert.Equal(McpToolCallOutcome.Failed, onlyCopy.Outcome);
        Assert.Contains("never published", onlyCopy.Error, StringComparison.Ordinal);
        _context.FilePageOperations.Verify(x => x.DiscardGitDraftAsync(It.Is<FilePageOperationRequest>(r => !r.DryRun), It.IsAny<ClaimsPrincipal>(),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    private async Task<FilePageDetails> ReadAsync(McpTestUser user, string storeId) =>
        (await _context.CallAsync(user, "search_site_pages", new { page_id = PageId, store_id = storeId })).EnsureSucceeded().Read<FilePageDetails>();

    private static object SetTitle(string title) => new { op = "set_section_settings", section_id = "hero", settings = new { title } };

    private static SiteBuilderChangePreview Preview(string field, string from, string to)
    {
        var preview = new SiteBuilderChangePreview { AffectsPublished = true };
        preview.AddObject("file-page", Path).AddField(field, from, to);
        return preview;
    }

    private static FilePageShipment ConflictShipment() => new()
    {
        GitFlow = true,
        State = GitPublishState.Conflict,
        CanRebase = true,
        ConflictError = "The page changed in main while this draft was being written.",
        Merge = new GitPublishResult { State = GitPublishState.Conflict, PullRequestNumber = 7, Url = "https://github.com/o/r/pull/7" },
    };

    private void SetUpPublish(FilePageShipment shipment)
    {
        _context.FilePageOperations
            .Setup(x => x.PublishAsync(It.IsAny<FilePagePublishOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilePagePublishOperationRequest request, ClaimsPrincipal _, CancellationToken _) => request.DryRun
                ? SiteBuilderOperationResult.DryRun<FilePageShipment>(Preview("status", "Draft", "Published"))
                : SiteBuilderOperationResult.Success(shipment, affectsPublished: true));
    }

    private void VerifyPublished(Times times) =>
        _context.FilePageOperations.Verify(x => x.PublishAsync(It.Is<FilePagePublishOperationRequest>(r => !r.DryRun), It.IsAny<ClaimsPrincipal>(),
            It.IsAny<CancellationToken>()), times);

    private void SetUpBlobDiscard(DraftDiscardStatus status)
    {
        _context.FilePageOperations
            .Setup(x => x.DiscardDraftAsync(It.IsAny<FileDraftDiscardOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileDraftDiscardOperationRequest request, ClaimsPrincipal _, CancellationToken _) => status == DraftDiscardStatus.OnlyCopy
                ? SiteBuilderOperationResult.Invalid(new FileDraftDiscard { Status = status }, "Nothing is published: the draft is the only copy.")
                : request.DryRun
                    ? SiteBuilderOperationResult.DryRun<FileDraftDiscard>(Preview("content", Published.Replace("Spring", "Summer", StringComparison.Ordinal), Published))
                    : SiteBuilderOperationResult.Success(new FileDraftDiscard { Status = status }));
    }

    private void VerifyDiscarded(Times times) =>
        _context.FilePageOperations.Verify(x => x.DiscardDraftAsync(It.Is<FileDraftDiscardOperationRequest>(r => !r.DryRun), It.IsAny<ClaimsPrincipal>(),
            It.IsAny<CancellationToken>()), times);
}
