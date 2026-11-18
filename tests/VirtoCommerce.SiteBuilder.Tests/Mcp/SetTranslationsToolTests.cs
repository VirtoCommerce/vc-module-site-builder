using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP.Jobs;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class SetTranslationsToolTests : IAsyncDisposable
{
    private const string Tool = "set_translations";
    private const string Content = """
        { "settings": { "seoInfo": { "pageTitle": { "en-US": "Home", "fr-FR": "" } } },
          "content": [ { "id": "h1", "type": "hero-banner", "title": { "en-US": "Spring", "fr-FR": "" } } ] }
        """;

    private static readonly DateTime _published = new(2026, 11, 10, 8, 30, 0, DateTimeKind.Utc);

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;
    private readonly List<string> _marked = [];
    private DateTime _sourceRevision = _published;
    private DateTime? _sourceRevisionAtTheSave;

    public SetTranslationsToolTests()
    {
        _pages = _context.UsePageStore();
        _context.SeedDraft("en", StoreA, Content, "en-US", multilanguagePageId: "ml1", sourceRevision: _published);
        _context.SeedDraft("fr", StoreA, Content, "fr-FR", multilanguagePageId: "ml1");
        _context.SeedDraft("de", StoreA, Content.Replace("fr-FR", "de-DE"), "de-DE", multilanguagePageId: "ml1");
        _context.SeedDraft("b1", StoreB, Content, "fr-FR");

        // A dry run checks the source revision as it is; the write checks it under its save's lock, which may come after the
        // job read it.
        _context.MultilanguagePageOperations
            .Setup(x => x.MarkUpToDateAsync(It.IsAny<MultilanguagePageMarkUpToDateOperationRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MultilanguagePageMarkUpToDateOperationRequest request, ClaimsPrincipal _, CancellationToken _) =>
            {
                if (request.GroupId == "b1")
                {
                    return SiteBuilderOperationResult.Invalid("Page 'b1' is not a language version.").As<GroupedSiteBuilderPage>();
                }

                if (request.ExpectedSourceRevisionDate != (request.DryRun ? _sourceRevision : _sourceRevisionAtTheSave ?? _sourceRevision))
                {
                    return SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.SourceRevisionChangedMessage).As<GroupedSiteBuilderPage>();
                }

                if (request.DryRun)
                {
                    return SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(null);
                }

                _marked.Add(request.GroupId);
                return SiteBuilderOperationResult.Success(new GroupedSiteBuilderPage { Id = request.GroupId });
            });
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public void TheTool_IsVisibleWithBuilderReadAndUpdate_Only()
    {
        var tool = _context.Harness.GetTool(Tool);

        Assert.True(_context.Harness.IsVisible(tool, Editor()));
        Assert.True(_context.Harness.IsVisible(tool, StoreAEditor()));
        Assert.False(_context.Harness.IsVisible(tool, Reader()));
    }

    [Fact]
    public async Task ADryRun_ChecksEveryItem_WritesNothing_AndShowsTheChange()
    {
        var preview = await CallAsync<TranslationPreview>(Editor(), new { translations = await FrenchAsync(), mark_up_to_date = true, dryRun = true });

        Assert.True(preview.DryRun);
        Assert.Equal((1, 2), (preview.PageCount, preview.TextCount));
        Assert.Contains(preview.Changes, x => x.Field.EndsWith("section hero-banner 'h1': title.fr-FR") && x.To?.ToString() == "Printemps");
        Assert.Contains(preview.Changes, x => x.Field.EndsWith("page settings: seoInfo.pageTitle.fr-FR") && x.To?.ToString() == "Accueil");
        Assert.Null(Assert.Single(preview.Pages).Problem);
        Assert.Empty(preview.Warnings);
        Assert.Equal(Content, await _context.DraftContentAsync("fr"));
        Assert.Empty(_context.SavedVersions.Records);
        Assert.Empty(_context.Jobs.Enqueued);
    }

    [Fact]
    public async Task ADryRun_ListsThePagesThatCannotTakeTheirTexts()
    {
        var items = new[]
        {
            Item("fr", "\"stale\"", "h1", "title", "Printemps"),
            Item("de", await _context.DraftVersionAsync("de"), "missing", "title", "Frühling", "de-DE"),
        };

        var preview = await CallAsync<TranslationPreview>(Editor(), new { translations = items, dryRun = true });

        Assert.Contains("changed since you read it", preview.Pages[0].Problem);
        Assert.Contains("no section 'missing'", preview.Pages[1].Problem);
    }

    [Fact]
    public async Task ARealRun_WithAProblem_IsRefused_AndQueuesNothing()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { translations = new[] { Item("fr", "\"stale\"", "h1", "title", "Printemps") } });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("nothing was saved", result.Error);
        Assert.Empty(_context.Jobs.Enqueued);
    }

    [Fact]
    public async Task ARealRun_QueuesOneJob_OneSavePerPage()
    {
        var items = (await FrenchAsync()).Append(Item("de", await _context.DraftVersionAsync("de"), "h1", "title", "Frühling", "de-DE")).ToArray();

        await CallAsync<McpJobHandle>(Editor(), new { translations = items, mark_up_to_date = true });

        var payload = Assert.IsType<SetTranslationsJobPayload>(Assert.Single(_context.Jobs.Enqueued).Payload);
        Assert.Equal(("editor", true), (payload.UserId, payload.MarkUpToDate));
        Assert.Equal(["fr", "de"], payload.Pages.Select(x => x.PageId));
        Assert.Equal(2, payload.Pages[0].Texts.Count);
        Assert.Equal(_published, payload.Pages[0].SourceRevisionDate);
    }

    [Fact]
    public async Task APageOfAnotherStore_IsRefused()
    {
        var result = await _context.CallAsync(StoreAEditor(), Tool,
            new { translations = new[] { Item("b1", await _context.DraftVersionAsync("b1"), "h1", "title", "Printemps") }, dryRun = true });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task APageStoredAsAFile_IsRefusedAsOne()
    {
        var result = await _context.CallAsync(Editor(), Tool,
            new { translations = new[] { Item("pages/about.page", "\"any\"", "h1", "title", "Printemps") }, dryRun = true });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("is a page stored as a file", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkingALandingPage_IsShownAsNotPossible()
    {
        var token = await _context.DraftVersionAsync("b1");

        var preview = await CallAsync<TranslationPreview>(Editor(),
            new { translations = new[] { Item("b1", token, "h1", "title", "Printemps") }, mark_up_to_date = true, dryRun = true });

        Assert.Contains("not a language version", Assert.Single(preview.Pages).NotMarkedUpToDate);
    }

    [Fact]
    public async Task TheJob_SavesEachPageOnce_AndMarksItUpToDate()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        var payload = await QueueAsync(await FrenchAsync());

        var run = await _context.RunJobAsync<SetTranslationsJob, SetTranslationsJobPayload>(payload);

        var saved = JsonNode.Parse(await _context.DraftContentAsync("fr"))!;
        Assert.Equal("Printemps", saved["content"]![0]!["title"]!["fr-FR"]!.GetValue<string>());
        Assert.Equal("Spring", saved["content"]![0]!["title"]!["en-US"]!.GetValue<string>());
        Assert.Equal("Accueil", saved["settings"]!["seoInfo"]!["pageTitle"]!["fr-FR"]!.GetValue<string>());
        Assert.Single(_context.SavedVersions.Records);
        Assert.Equal(["fr"], _marked);
        Assert.Contains("1 pages saved", run.FinalMessage);
        Assert.Contains("1 marked up to date", run.FinalMessage);
    }

    [Fact]
    public async Task ASourcePublishedAfterTheTranslation_LeavesItOutdated()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        var payload = await QueueAsync(await FrenchAsync());
        _sourceRevision = _published.AddHours(2);

        var run = await _context.RunJobAsync<SetTranslationsJob, SetTranslationsJobPayload>(payload);

        Assert.Single(_context.SavedVersions.Records);
        Assert.Empty(_marked);
        Assert.Contains("not marked: fr (the source language version was published again after you read it)", run.FinalMessage);
    }

    [Fact]
    public async Task ASourcePublishedAgainBeforeTheMarkIsSaved_LeavesItOutdated()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        var payload = await QueueAsync(await FrenchAsync());
        _sourceRevisionAtTheSave = _published.AddHours(2);

        var run = await _context.RunJobAsync<SetTranslationsJob, SetTranslationsJobPayload>(payload);

        Assert.Single(_context.SavedVersions.Records);
        Assert.Empty(_marked);
        Assert.Contains("not marked: fr (the source language version was published again after you read it)", run.FinalMessage);
    }

    [Fact]
    public async Task TheJob_MarksThroughTheDesignersMarkUpToDate_AsTheUser_AtTheSourceRevisionTheAgentRead()
    {
        var editor = Editor().ToPrincipal();
        _context.JobUsers["editor"] = editor;
        var payload = await QueueAsync(await FrenchAsync());

        await _context.RunJobAsync<SetTranslationsJob, SetTranslationsJobPayload>(payload);

        _context.MultilanguagePageOperations.Verify(x => x.MarkUpToDateAsync(
            It.Is<MultilanguagePageMarkUpToDateOperationRequest>(r => r.GroupId == "fr" && r.ExpectedSourceRevisionDate == _published && !r.DryRun),
            editor, It.IsAny<CancellationToken>()), Times.Once);
        _context.MultilanguagePages.Verify(x => x.MarkUpToDateAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ADryRun_ShowsTheProblemTheDesignersMarkUpToDateTells()
    {
        _sourceRevision = _published.AddHours(2);

        var preview = await CallAsync<TranslationPreview>(Editor(), new { translations = await FrenchAsync(), mark_up_to_date = true, dryRun = true });

        Assert.Equal("the source language version was published again after you read it", Assert.Single(preview.Pages).NotMarkedUpToDate);
        _context.MultilanguagePageOperations.Verify(x => x.MarkUpToDateAsync(
            It.Is<MultilanguagePageMarkUpToDateOperationRequest>(r => r.GroupId == "fr" && r.ExpectedSourceRevisionDate == _published && r.DryRun),
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task APageSavedAfterTheCall_IsSkipped_AndNotMarked()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        var payload = await QueueAsync(await FrenchAsync());
        _pages.SeedContent("fr-draft", Content.Replace("Spring", "Summer"));

        var run = await _context.RunJobAsync<SetTranslationsJob, SetTranslationsJobPayload>(payload);

        Assert.Empty(_context.SavedVersions.Records);
        Assert.Empty(_marked);
        Assert.Contains("1 skipped because they changed since the call: fr", run.FinalMessage);
    }

    [Fact]
    public async Task AStoreTheUserLostSinceTheCall_IsRefused()
    {
        _context.JobUsers["editor"] = McpTestUser.Create("editor").WithScopedPermission(Permissions.Read, StoreScope, StoreB).WithPermissions(Permissions.Update).ToPrincipal();
        var payload = await QueueAsync(await FrenchAsync());

        var run = await _context.RunJobAsync<SetTranslationsJob, SetTranslationsJobPayload>(payload);

        Assert.Empty(_context.SavedVersions.Records);
        Assert.Contains("1 refused: fr (no builder:read in store 'store-a')", run.FinalMessage);
    }

    [Fact]
    public async Task ALockedOutAccount_StopsTheJob_AndMarksNothing()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        var payload = await QueueAsync(await FrenchAsync());
        _context.LockedOutUsers.Add("editor");

        var run = await _context.RunJobAsync<SetTranslationsJob, SetTranslationsJobPayload>(payload);

        Assert.Empty(_context.SavedVersions.Records);
        Assert.Empty(_marked);
        Assert.StartsWith($"Stopped after 1 of 1 pages: {SiteBuilderJobUsers.AccountGone}", run.FinalMessage, StringComparison.Ordinal);
        Assert.Contains($"1 refused: fr ({SiteBuilderJobUsers.AccountGone})", run.FinalMessage);
    }

    private async Task<SetTranslationsJobPayload> QueueAsync(object[] items)
    {
        await CallAsync<McpJobHandle>(Editor(), new { translations = items, mark_up_to_date = true });
        return (SetTranslationsJobPayload)_context.Jobs.Enqueued.Single().Payload;
    }

    private async Task<object[]> FrenchAsync()
    {
        var token = await _context.DraftVersionAsync("fr");
        return
        [
            Item("fr", token, "h1", "title", "Printemps"),
            Item("fr", token, null, "seoInfo.pageTitle", "Accueil"),
        ];
    }

    private static object Item(string pageId, string token, string sectionId, string path, string text, string language = "fr-FR") => new
    {
        page_id = pageId,
        version_token = token,
        section_id = sectionId,
        setting_path = path,
        language,
        text,
        source_revision_date = _published,
    };

    private async Task<T> CallAsync<T>(McpTestUser user, object arguments)
    {
        var result = await _context.CallAsync(user, Tool, arguments);
        return result.EnsureSucceeded().Read<T>();
    }
}
