using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.MCP.Jobs;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class UpdateSectionsBulkToolTests : IAsyncDisposable
{
    private const string Tool = "update_sections_bulk";
    private const string WithHero = """{ "settings": {}, "content": [ { "id": "t1", "type": "text" }, { "id": "h1", "type": "hero-banner", "title": "Black Friday" } ] }""";

    private readonly McpTestContext _context = new();
    private readonly PublishedRenameContentPreservationTests.FakeGroupedPageService _pages;
    private readonly List<string> _ids = [];

    public UpdateSectionsBulkToolTests()
    {
        _pages = _context.UsePageStore();
        _context.ListPages(() => _pages.GetAsync(_ids).GetAwaiter().GetResult().Select(LandingRow));

        Seed("a1", StoreA, WithHero);
        Seed("a2", StoreA, """{ "settings": {}, "content": [ { "id": "t1", "type": "text" } ] }""");
        Seed("a3", StoreA, WithHero.Replace("Black Friday", "Spring"));
        Seed("b1", StoreB, WithHero);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Editor(string name = "editor") => McpTestUser.Create(name).WithPermissions(Permissions.Read, Permissions.Update);

    private static McpTestUser StoreAEditor() =>
        McpTestUser.Create("store-a-editor").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update);

    private static object Rename() => new
    {
        section_type = "hero-banner",
        setting_path = "title",
        setting_contains = "friday",
        change = "set_section_settings",
        settings = new { title = "Cyber Monday" },
    };

    [Fact]
    public void TheTool_IsVisibleWithBuilderReadAndUpdate_Only()
    {
        var tool = _context.Harness.GetTool(Tool);

        Assert.True(_context.Harness.IsVisible(tool, Editor()));
        Assert.True(_context.Harness.IsVisible(tool, StoreAEditor()));
        Assert.False(_context.Harness.IsVisible(tool, Reader()));
        Assert.False(_context.Harness.IsVisible(tool, McpTestUser.Create("updater").WithPermissions(Permissions.Update)));
    }

    [Fact]
    public async Task ADryRun_WritesNothing_AndShowsTheChangeWithTheCoverage()
    {
        var before = await ContentAsync("a1");

        var preview = await CallAsync<SectionBulkPreview>(Editor(), new
        {
            section_type = "hero-banner",
            setting_path = "title",
            setting_contains = "friday",
            change = "set_section_settings",
            settings = new { title = "Cyber Monday" },
            dryRun = true,
        });

        Assert.True(preview.DryRun);
        Assert.Equal((4, 4, true, 2, 2), (preview.TotalPageCount, preview.ScannedPageCount, preview.IsComplete, preview.TargetPageCount, preview.TargetSectionCount));
        Assert.Equal(["a1", "b1"], preview.Pages.Select(x => x.PageId));
        Assert.Contains(preview.Changes, x => x.Field.EndsWith("section hero-banner 'h1': title") && x.From?.ToString() == "Black Friday" && x.To?.ToString() == "Cyber Monday");
        Assert.Equal(before, await ContentAsync("a1"));
        Assert.Empty(_context.SavedVersions.Records);
        Assert.Empty(_context.Jobs.Enqueued);
    }

    [Fact]
    public async Task ARealRun_QueuesOneJob_WithThePagesAndVersionsOfTheCall()
    {
        var handle = await CallAsync<McpJobHandle>(Editor(), Rename());

        var (handler, payload, request) = Assert.Single(_context.Jobs.Enqueued);
        var job = Assert.IsType<UpdateSectionsBulkJobPayload>(payload);
        Assert.Equal(typeof(UpdateSectionsBulkJob), handler);
        Assert.Equal("editor", job.UserId);
        Assert.Equal(("set_section_settings", "hero-banner"), (job.Change, job.SectionType));
        Assert.Equal(["a1", "b1"], job.Pages.Select(x => x.PageId));
        Assert.Equal(await TokenAsync("a1"), job.Pages[0].VersionToken);
        Assert.Equal(["h1"], job.Pages[0].SectionIds);
        Assert.Equal(2, request.TotalCount);
        Assert.Equal("job-1", handle.JobId);
        Assert.Empty(_context.SavedVersions.Records);
    }

    [Fact]
    public async Task AStoreEditor_ChangesOnlyTheirStore()
    {
        await CallAsync<McpJobHandle>(StoreAEditor(), Rename());

        var job = (UpdateSectionsBulkJobPayload)Assert.Single(_context.Jobs.Enqueued).Payload;
        Assert.Equal(["a1"], job.Pages.Select(x => x.PageId));
    }

    [Fact]
    public async Task AStoreEditor_IsRefusedAnotherStore()
    {
        var result = await _context.CallAsync(StoreAEditor(), Tool, new { store_ids = new[] { StoreB }, section_type = "hero-banner", change = "hide_section" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Empty(_context.Jobs.Enqueued);
    }

    [Fact]
    public async Task SectionsOfASharedComponent_AreNotChanged()
    {
        _context.SharedComponents
            .Setup(x => x.GetAsync(It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync([new SiteBuilderSharedComponent { Id = "sc1", StoreId = StoreA }]);
        _context.SharedComponentContents
            .Setup(x => x.TryLoadContentAsync(It.IsAny<SiteBuilderSharedComponent>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync("""{ "settings": {}, "content": [ { "id": "c1", "type": "hero-banner" } ] }""");
        Seed("a4", StoreA, """{ "settings": {}, "content": [ { "id": "pl1", "type": "componentRef", "componentRef": "sc1" } ] }""");
        var user = McpTestUser.Create("components").WithPermissions(Permissions.Read, Permissions.Update, Permissions.SharedComponentsRead);

        var preview = await CallAsync<SectionBulkPreview>(user, new { store_ids = new[] { StoreA }, section_type = "hero-banner", change = "hide_section", dryRun = true });

        Assert.Equal(1, preview.SharedComponentSectionCount);
        Assert.DoesNotContain("a4", preview.Pages.Select(x => x.PageId));
        Assert.Contains(preview.Warnings, x => x.Contains("Shared Components"));
    }

    [Fact]
    public async Task WithoutBackgroundJobs_TheAgentIsToldInASentence()
    {
        _context.Jobs.Failure = new BackgroundJobEngineNotInstalledException();

        var result = await _context.CallAsync(Editor(), Tool, Rename());

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.StartsWith("Background jobs are not available on this platform", result.Error);
        Assert.Contains("nothing was changed", result.Error);
        Assert.Contains("edit_page_draft", result.Error);
    }

    [Fact]
    public async Task AnUnknownChange_IsRefused()
    {
        var result = await _context.CallAsync(Editor(), Tool, new { section_type = "hero-banner", change = "delete_page" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("change must be one of", result.Error);
    }

    [Fact]
    public async Task TheJob_ChangesThePagesAsTheUser_AndReportsEachPage()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        await CallAsync<McpJobHandle>(Editor(), Rename());
        var payload = (UpdateSectionsBulkJobPayload)_context.Jobs.Enqueued.Single().Payload;

        var run = await _context.RunJobAsync<UpdateSectionsBulkJob, UpdateSectionsBulkJobPayload>(payload);

        Assert.Equal("Cyber Monday", Title(await ContentAsync("a1")));
        Assert.Equal("Cyber Monday", Title(await ContentAsync("b1")));
        Assert.Equal("Spring", Title(await ContentAsync("a3")));
        Assert.Equal([1L, 2L, 2L], run.Reports.Select(x => x.ProcessedCount ?? 0));
        Assert.Contains("2 pages changed", run.FinalMessage);
        Assert.Equal(2, _context.SavedVersions.Records.Count);
        _context.UserNames.Verify(x => x.SetCurrentUserName("editor"));
    }

    [Fact]
    public async Task APageSavedAfterTheCall_IsSkipped_NotOverwritten()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        await CallAsync<McpJobHandle>(Editor(), Rename());
        var payload = (UpdateSectionsBulkJobPayload)_context.Jobs.Enqueued.Single().Payload;
        _pages.SeedContent("a1-draft", WithHero.Replace("\"t1\"", "\"t9\""));

        var run = await _context.RunJobAsync<UpdateSectionsBulkJob, UpdateSectionsBulkJobPayload>(payload);

        Assert.Equal("Black Friday", Title(await ContentAsync("a1")));
        Assert.Equal("Cyber Monday", Title(await ContentAsync("b1")));
        Assert.Contains("1 skipped because they changed since the call: a1", run.FinalMessage);
    }

    [Fact]
    public async Task AStoreTheUserLostSinceTheCall_IsRefused()
    {
        await CallAsync<McpJobHandle>(Editor(), Rename());
        var payload = (UpdateSectionsBulkJobPayload)_context.Jobs.Enqueued.Single().Payload;
        _context.JobUsers["editor"] = StoreAEditor().ToPrincipal();

        var run = await _context.RunJobAsync<UpdateSectionsBulkJob, UpdateSectionsBulkJobPayload>(payload);

        Assert.Equal("Cyber Monday", Title(await ContentAsync("a1")));
        Assert.Equal("Black Friday", Title(await ContentAsync("b1")));
        Assert.Contains("1 pages changed", run.FinalMessage);
        Assert.Contains("1 refused: b1 (no builder:read in store 'store-b')", run.FinalMessage);
    }

    [Fact]
    public async Task AUserWhoLostBuilderUpdate_ChangesNothing()
    {
        await CallAsync<McpJobHandle>(Editor(), Rename());
        var payload = (UpdateSectionsBulkJobPayload)_context.Jobs.Enqueued.Single().Payload;
        _context.JobUsers["editor"] = Reader("editor").ToPrincipal();

        var run = await _context.RunJobAsync<UpdateSectionsBulkJob, UpdateSectionsBulkJobPayload>(payload);

        Assert.Empty(_context.SavedVersions.Records);
        Assert.Contains("0 pages changed", run.FinalMessage);
        Assert.Contains("2 refused", run.FinalMessage);
        Assert.Contains("no longer has builder:update", run.FinalMessage);
    }

    [Fact]
    public async Task AnAccountThatNoLongerExists_ChangesNothing()
    {
        await CallAsync<McpJobHandle>(Editor(), Rename());
        var payload = (UpdateSectionsBulkJobPayload)_context.Jobs.Enqueued.Single().Payload;

        var run = await _context.RunJobAsync<UpdateSectionsBulkJob, UpdateSectionsBulkJobPayload>(payload);

        Assert.Empty(_context.SavedVersions.Records);
        Assert.Contains("no longer exists or is locked", run.FinalMessage);
    }

    [Fact]
    public async Task BuilderUpdateTakenAwayDuringTheJob_RefusesTheNextPage()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        await CallAsync<McpJobHandle>(Editor(), Rename());
        var payload = (UpdateSectionsBulkJobPayload)_context.Jobs.Enqueued.Single().Payload;

        var run = await _context.RunJobAsync<UpdateSectionsBulkJob, UpdateSectionsBulkJobPayload>(payload,
            _ => _context.JobUsers["editor"] = Reader("editor").ToPrincipal());

        Assert.Equal("Cyber Monday", Title(await ContentAsync("a1")));
        Assert.Equal("Black Friday", Title(await ContentAsync("b1")));
        Assert.StartsWith("Done", run.FinalMessage, StringComparison.Ordinal);
        Assert.Contains("1 pages changed", run.FinalMessage);
        Assert.Contains("1 refused: b1 (the account no longer has builder:update)", run.FinalMessage);
    }

    [Fact]
    public async Task AnAccountDeletedDuringTheJob_StopsIt_AtTheNextPage()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        await CallAsync<McpJobHandle>(Editor(), Rename());
        var payload = (UpdateSectionsBulkJobPayload)_context.Jobs.Enqueued.Single().Payload;

        var run = await _context.RunJobAsync<UpdateSectionsBulkJob, UpdateSectionsBulkJobPayload>(payload, _ => _context.DeletedUsers.Add("editor"));

        Assert.Equal("Cyber Monday", Title(await ContentAsync("a1")));
        Assert.Equal("Black Friday", Title(await ContentAsync("b1")));
        Assert.StartsWith($"Stopped after 2 of 2 pages: {SiteBuilderJobUsers.AccountGone}; 0 pages were not reached.", run.FinalMessage, StringComparison.Ordinal);
        Assert.Contains($"1 refused: b1 ({SiteBuilderJobUsers.AccountGone})", run.FinalMessage);
    }

    [Fact]
    public async Task ALockedOutAccount_StopsTheJob_BeforeItChangesAPage()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        await CallAsync<McpJobHandle>(Editor(), Rename());
        var payload = (UpdateSectionsBulkJobPayload)_context.Jobs.Enqueued.Single().Payload;
        _context.LockedOutUsers.Add("editor");

        var run = await _context.RunJobAsync<UpdateSectionsBulkJob, UpdateSectionsBulkJobPayload>(payload);

        Assert.Empty(_context.SavedVersions.Records);
        Assert.Equal([1L, 1L], run.Reports.Select(x => x.ProcessedCount ?? 0));
        Assert.StartsWith($"Stopped after 1 of 2 pages: {SiteBuilderJobUsers.AccountGone}; 1 pages were not reached.", run.FinalMessage, StringComparison.Ordinal);
    }

    private async Task<T> CallAsync<T>(McpTestUser user, object arguments)
    {
        var result = await _context.CallAsync(user, Tool, arguments);
        return result.EnsureSucceeded().Read<T>();
    }

    private static string Title(string content) =>
        SiteBuilderPageItems.GetString(JsonNode.Parse(content)!["content"]!.AsArray().First(x => x!["id"]!.GetValue<string>() == "h1")!["title"]);

    private async Task<string> ContentAsync(string id)
    {
        var group = (await _pages.GetAsync([id])).Single();
        return (await _pages.LoadGroupContentAsync(group, cancellationToken: TestContext.Current.CancellationToken)).Content;
    }

    private async Task<string> TokenAsync(string id)
    {
        var group = (await _pages.GetAsync([id])).Single();
        return (await _pages.LoadGroupContentAsync(group, cancellationToken: TestContext.Current.CancellationToken)).ETag;
    }

    private void Seed(string id, string storeId, string content)
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = id,
            StoreId = storeId,
            Name = $"Page {id}",
            Permalink = $"/{id}",
            CultureName = "en-US",
            Pages = [new SiteBuilderPage { Id = $"{id}-draft", GroupId = id, StoreId = storeId, Status = "Draft" }],
        });
        _pages.SeedContent($"{id}-draft", content);
        _ids.Add(id);
    }
}
