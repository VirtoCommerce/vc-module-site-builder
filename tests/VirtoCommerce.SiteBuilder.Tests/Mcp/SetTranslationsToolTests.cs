using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
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

    public SetTranslationsToolTests()
    {
        _pages = _context.UsePageStore();
        Seed("en", StoreA, "en-US", Content, multilanguagePageId: "ml1", sourceRevision: _published);
        Seed("fr", StoreA, "fr-FR", Content, multilanguagePageId: "ml1");
        Seed("de", StoreA, "de-DE", Content.Replace("fr-FR", "de-DE"), multilanguagePageId: "ml1");
        Seed("b1", StoreB, "fr-FR", Content);

        _context.MultilanguagePages
            .Setup(x => x.GetLanguagesAsync("ml1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            [
                new MultilanguagePageLanguage { CultureName = "en-US", IsDefault = true, Page = new GroupedSiteBuilderPage { Id = "en", SourceRevisionDate = _sourceRevision } },
                new MultilanguagePageLanguage { CultureName = "fr-FR", Page = new GroupedSiteBuilderPage { Id = "fr" } },
            ]);
        _context.MultilanguagePages
            .Setup(x => x.MarkUpToDateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string id, CancellationToken _) => _marked.Add(id))
            .ReturnsAsync((string id, CancellationToken _) => new GroupedSiteBuilderPage { Id = id });
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Editor(string name = "editor") => McpTestUser.Create(name).WithPermissions(Permissions.Read, Permissions.Update);

    private static McpTestUser StoreAEditor() =>
        McpTestUser.Create("store-a-editor").WithScopedPermission(Permissions.Read, StoreScope, StoreA).WithPermissions(Permissions.Update);

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
        Assert.Equal(Content, await ContentAsync("fr"));
        Assert.Empty(_context.SavedVersions.Records);
        Assert.Empty(_context.Jobs.Enqueued);
    }

    [Fact]
    public async Task ADryRun_ListsThePagesThatCannotTakeTheirTexts()
    {
        var items = new[]
        {
            Item("fr", "\"stale\"", "h1", "title", "Printemps"),
            Item("de", await TokenAsync("de"), "missing", "title", "Frühling", "de-DE"),
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
        var items = (await FrenchAsync()).Append(Item("de", await TokenAsync("de"), "h1", "title", "Frühling", "de-DE")).ToArray();

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
            new { translations = new[] { Item("b1", await TokenAsync("b1"), "h1", "title", "Printemps") }, dryRun = true });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task MarkingALandingPage_IsShownAsNotPossible()
    {
        var preview = await CallAsync<TranslationPreview>(Editor(),
            new { translations = new[] { Item("b1", await TokenAsync("b1"), "h1", "title", "Printemps") }, mark_up_to_date = true, dryRun = true });

        Assert.Contains("not a language version", Assert.Single(preview.Pages).NotMarkedUpToDate);
    }

    [Fact]
    public async Task TheJob_SavesEachPageOnce_AndMarksItUpToDate()
    {
        _context.JobUsers["editor"] = Editor().ToPrincipal();
        var payload = await QueueAsync(await FrenchAsync());

        var run = await _context.RunJobAsync<SetTranslationsJob, SetTranslationsJobPayload>(payload);

        var saved = JsonNode.Parse(await ContentAsync("fr"))!;
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

    private async Task<SetTranslationsJobPayload> QueueAsync(object[] items)
    {
        await CallAsync<McpJobHandle>(Editor(), new { translations = items, mark_up_to_date = true });
        return (SetTranslationsJobPayload)_context.Jobs.Enqueued.Single().Payload;
    }

    private async Task<object[]> FrenchAsync()
    {
        var token = await TokenAsync("fr");
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

    private void Seed(string id, string storeId, string language, string content, string multilanguagePageId = null, DateTime? sourceRevision = null)
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = id,
            StoreId = storeId,
            Name = $"Page {id}",
            Permalink = $"/{id}",
            CultureName = language,
            MultilanguagePageId = multilanguagePageId,
            SourceRevisionDate = sourceRevision,
            Pages = [new SiteBuilderPage { Id = $"{id}-draft", GroupId = id, StoreId = storeId, Status = "Draft" }],
        });
        _pages.SeedContent($"{id}-draft", content);
    }
}
