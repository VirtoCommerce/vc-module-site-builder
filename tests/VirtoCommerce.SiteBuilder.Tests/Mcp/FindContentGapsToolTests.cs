using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class FindContentGapsToolTests : IAsyncDisposable
{
    private const string Tool = "find_content_gaps";

    private const string WithSeo =
        """{ "settings": { "seoInfo": { "pageTitle": "Home", "metaDescription": { "en-US": "Our home" } } }, "content": [] }""";

    private readonly McpTestContext _context = new();
    private readonly List<SiteBuilderPageListItem> _rows = [];

    public FindContentGapsToolTests()
    {
        _context.UsePageStore();
        _rows.Add(LandingRow(_context.SeedPage("no-seo", StoreA, published: true, draft: false)));
        _rows.Add(LandingRow(_context.SeedPage("with-seo", StoreA, published: true, draft: false, content: WithSeo)));
        _rows.Add(LandingRow(_context.SeedPage("b-page", StoreB, published: true, draft: false, content: WithSeo)));

        var french = _context.SeedPage("fr-version", StoreA, published: true, draft: false, content: WithSeo);
        _rows.Add(new SiteBuilderPageListItem
        {
            Type = "Multilanguage",
            Id = "root",
            Name = "Root root",
            MultilanguagePage = Root("root", StoreA),
            Languages =
            [
                new MultilanguagePageLanguage { CultureName = "en-US", IsDefault = true, IsStoreLanguage = true, Status = "Published" },
                new MultilanguagePageLanguage { CultureName = "de-DE", IsStoreLanguage = true, Status = "Missing" },
                new MultilanguagePageLanguage { CultureName = "fr-FR", IsStoreLanguage = true, Status = "Published", IsOutdated = true, Page = french },
            ],
        });
        _context.ListPages(() => _rows);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private async Task<ContentGapReport> FindAsync(McpTestUser user, object arguments) =>
        (await _context.CallAsync(user, Tool, arguments)).EnsureSucceeded().Read<ContentGapReport>();

    [Fact]
    public void BuilderRead_ShowsTheTool()
    {
        Assert.True(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), StoreAReader()));
        Assert.False(_context.Harness.IsVisible(_context.Harness.GetTool(Tool), McpTestUser.Create("none").WithPermissions(AssetRead)));
    }

    [Fact]
    public async Task TheGaps_OfAStore_AreFindings_WithEvidenceAndAFix()
    {
        var report = await FindAsync(Reader(), new { store_id = StoreA });

        Assert.Contains(report.Findings, x => x.Id == "translation-missing" && x.ObjectId == "root" && x.Title.Contains("de-DE", StringComparison.Ordinal));
        Assert.Contains(report.Findings, x => x.Id == "translation-outdated" && x.ObjectId == "fr-version");
        Assert.Contains(report.Findings, x => x.Id == "seo-title-missing" && x.ObjectId == "no-seo" && x.Severity == McpFindingSeverities.Warning);
        Assert.Contains(report.Findings, x => x.Id == "seo-description-missing" && x.ObjectId == "no-seo");
        Assert.DoesNotContain(report.Findings, x => x.ObjectId == "with-seo" || x.ObjectId == "b-page");
        Assert.All(report.Findings, x => Assert.False(string.IsNullOrEmpty(x.Evidence) || string.IsNullOrEmpty(x.Fix)));
        Assert.True(report.IsComplete);
    }

    [Fact]
    public async Task TheUncheckableCategories_AreNamedNotAvailable_WithTheReason()
    {
        var report = await FindAsync(Reader(), new { store_id = StoreA, category = "references" });

        Assert.Empty(report.Findings);
        var check = Assert.Single(report.NotChecked);
        Assert.Equal("references", check.Category);
        Assert.StartsWith("Not available:", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCall_SaysWhatItDoesNotCheck()
    {
        var report = await FindAsync(Reader(), new { store_id = StoreA, category = "seo" });

        Assert.Empty(report.NotChecked);
        Assert.DoesNotContain(report.Findings, x => x.Category == "translations");

        var all = await FindAsync(Reader(), new { store_id = StoreA });
        Assert.Equal(["references", "schema"], all.NotChecked.Select(x => x.Category));
    }

    [Fact]
    public async Task AReaderOfStoreA_IsRefusedStoreB_AndWithoutAStoreReadsStoreAOnly()
    {
        var refused = await _context.CallAsync(StoreAReader(), Tool, new { store_id = StoreB });
        var report = await FindAsync(StoreAReader(), new { });

        Assert.Equal(McpToolCallOutcome.Forbidden, refused.Outcome);
        Assert.Equal(3, report.TotalCount);
        Assert.DoesNotContain(report.Findings, x => x.ObjectId == "b-page");
    }

    [Fact]
    public async Task TwoPages_HoldEveryPageOnce()
    {
        var first = await FindAsync(Reader(), new { store_id = StoreA, take = 2 });
        var second = await FindAsync(Reader(), new { store_id = StoreA, skip = first.NextSkip, take = 2 });

        Assert.Equal(2, first.ScannedCount);
        Assert.False(first.IsComplete);
        Assert.Equal(1, second.ScannedCount);
        Assert.True(second.IsComplete);
        Assert.Contains("every page was read", second.Coverage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownCategory_IsRefused()
    {
        var result = await _context.CallAsync(Reader(), Tool, new { category = "images" });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
    }
}
