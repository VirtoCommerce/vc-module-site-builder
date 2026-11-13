using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class GetPreviewToolTests : IAsyncDisposable
{
    private const string Tool = "get_preview";
    private const string Rules = """
        { "version": 2,
          "product": [ { "id": "bikes", "template": "product.bicycles", "when": { "inCategory": ["bikes"] } } ],
          "page": [
            { "id": "blog", "template": "page.blog", "when": { "path": "/blog/*" } },
            { "id": "off", "template": "page.landing", "disabled": true },
            { "id": "landing", "template": "page.landing", "when": { "path": "/landing*", "language": "en", "source": "Pages" } }
          ] }
        """;

    private readonly McpTestContext _context = new();

    public GetPreviewToolTests()
    {
        _context.UsePageStore();
        _context.SeedPage("landing-spring", StoreA, published: true, draft: true);
        _context.SeedPage("b1", StoreB, published: true, draft: false);
        _context.Previews
            .Setup(x => x.GetGroupedPagePreviewAsync(It.IsAny<GroupedPageQueryRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupedPageQueryRequest request, ClaimsPrincipal _, CancellationToken _) =>
                SiteBuilderOperationResult.Success(new SiteBuilderPreviewLink { Url = $"https://shop.test/designer-preview?pageId={request.GroupId}" }));
        _context.Previews
            .Setup(x => x.GetTemplatePreviewAsync(It.IsAny<SiteBuilderTemplatePreviewRequest>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SiteBuilderOperationResult.Success(new SiteBuilderPreviewLink { Url = "https://shop.test/product/sample" }));
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private async Task SeedAsync()
    {
        await _context.SeedThemeAsync(StoreA);
        _context.SeedThemeFile(StoreA, "templates/product.bicycles.json", ProductTemplate);
        _context.SeedThemeFile(StoreA, "templates/page.blog.json", """{ "settings": {}, "content": [ { "id": "p", "type": "page-content" } ] }""");
        _context.SeedThemeFile(StoreA, "templates/page.landing.json", """{ "settings": {}, "content": [ { "id": "p", "type": "page-content" } ] }""");
        _context.SeedThemeFile(StoreA, "config/template_rules.json", Rules);
    }

    [Fact]
    public async Task APage_GetsItsPreviewLink_AndTheTemplateItsRulesPick()
    {
        await SeedAsync();

        var answer = (await _context.CallAsync(Reader(), Tool, new { page_id = "landing-spring" })).EnsureSucceeded().Read<PreviewAnswer>();

        Assert.Equal("https://shop.test/designer-preview?pageId=landing-spring", answer.PreviewUrl);
        Assert.Equal(("page.landing", "landing", true), (answer.Template.Template, answer.Template.RuleId, answer.Template.Decided));
        Assert.Equal([RuleOutcome.DoesNotMatch, RuleOutcome.Skipped, RuleOutcome.Matches], answer.Template.Rules.Select(x => x.Outcome));
        Assert.Empty(answer.Template.NotEvaluated);
        Assert.Contains("not a rendering", answer.Template.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProduct_GetsTheRulesInOrder_WithTheConditionsTheServerDoesNotEvaluate()
    {
        await SeedAsync();

        var answer = (await _context.CallAsync(Reader(), Tool, new { store_id = StoreA, product_id = "p1" })).EnsureSucceeded().Read<PreviewAnswer>();

        Assert.False(answer.Template.Decided);
        Assert.Equal("product", answer.Template.Template);
        Assert.Equal(["inCategory"], answer.Template.NotEvaluated);
        Assert.Equal(RuleOutcome.MayMatch, Assert.Single(answer.Template.Rules).Outcome);
        Assert.Contains("bikes", answer.Template.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATemplate_GetsItsSamplePage_AndTheRulesThatGiveItPages()
    {
        await SeedAsync();

        var answer = (await _context.CallAsync(Reader(), Tool, new { store_id = StoreA, template_key = "product.bicycles" })).EnsureSucceeded().Read<PreviewAnswer>();

        Assert.Equal("https://shop.test/product/sample", answer.PreviewUrl);
        Assert.Single(answer.PickedBy, x => x.StartsWith("bikes:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AUserOfStoreA_CannotPreviewInStoreB()
    {
        await _context.SeedThemeAsync(StoreB);

        var page = await _context.CallAsync(StoreAReader(), Tool, new { page_id = "b1" });
        var product = await _context.CallAsync(StoreAReader(), Tool, new { store_id = StoreB, product_id = "p1" });

        Assert.Equal(McpToolCallOutcome.Forbidden, page.Outcome);
        Assert.Equal(McpToolCallOutcome.Forbidden, product.Outcome);
    }
}
