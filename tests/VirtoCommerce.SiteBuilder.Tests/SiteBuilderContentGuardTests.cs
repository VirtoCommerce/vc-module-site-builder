using System.Linq;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderContentGuardTests
{
    private static readonly JObject Catalog = JObject.Parse("""
        {
          "sections": {
            "hero": {
              "settings": [
                { "id": "link", "type": "string", "format": "url" },
                { "id": "image", "type": "images" },
                { "id": "body", "type": "text" },
                { "id": "raw", "type": "html" },
                { "id": "text", "type": "markdown" },
                { "id": "summary", "type": "markdown", "resultType": "html" },
                { "id": "source", "type": "markdown", "resultType": "markdown" },
                { "id": "title", "type": "string" },
                { "id": "buttons", "type": "list", "element": [ { "id": "url", "type": "url" }, { "id": "label", "type": "string" } ] }
              ]
            }
          },
          "blocks": { "card": { "settings": [ { "id": "caption", "type": "text" } ] } },
          "shared": {},
          "paletteTokens": { "light": [ "color_primary_500" ], "dark": [ "color_primary_500", "color_dark_only" ] }
        }
        """);

    private readonly SiteBuilderContentGuard _guard = new();

    [Theory]
    [InlineData("javascript:alert(1)", "javascript")]
    [InlineData("JAVASCRIPT:alert(1)", "javascript")]
    [InlineData("JaVaScRiPt:alert(1)", "javascript")]
    [InlineData("  javascript:alert(1)", "javascript")]
    [InlineData("\u0000\u0001\u001f javascript:alert(1) \u0000", "javascript")]
    [InlineData("java\tscript:alert(1)", "javascript")]
    [InlineData("java\nscript:alert(1)", "javascript")]
    [InlineData("\tjav\r\nascript:alert(1)", "javascript")]
    [InlineData("&#106;avascript:alert(1)", "javascript")]
    [InlineData("&#106avascript:alert(1)", "javascript")]
    [InlineData("&#x6A;&#x61;vascript&#x3A;alert(1)", "javascript")]
    [InlineData("javascript&colon;alert(1)", "javascript")]
    [InlineData("java&Tab;script:alert(1)", "javascript")]
    [InlineData("vbscript:msgbox(1)", "vbscript")]
    [InlineData("VBScript:msgbox(1)", "vbscript")]
    [InlineData(" vb\tscript:msgbox(1)", "vbscript")]
    public void GuardPage_ScriptUrlInALink_ReturnsAnErrorNamingThePath(string value, string scheme)
    {
        // the unknown type has no schema, so its keys decide; the known one is checked by its schema
        var unknown = Page(new JObject
        {
            ["id"] = "a",
            ["type"] = "unknown",
            ["cta"] = new JObject { ["buttonLink"] = value },
            ["slides"] = new JArray(new JObject { ["url"] = "/ok" }, new JObject { ["url"] = value }),
            ["backgroundImage"] = value,
            ["logo_image"] = value,
            ["permalink"] = value,
            ["href"] = new JArray("/ok", value),
            ["src"] = new JObject { ["en-US"] = value },
        });
        var known = Page(new JObject
        {
            ["id"] = "a",
            ["type"] = "hero",
            ["link"] = value,
            ["buttons"] = new JArray(new JObject { ["url"] = value, ["label"] = "Go" }),
            ["image"] = value,
        });

        Assert.Equal(
            new[] { "cta.buttonLink", "slides[1].url", "backgroundImage", "logo_image", "permalink", "href[1]", "src.en-US" }
                .Select(x => $"page: 'content[0].{x}' holds a {scheme}: URL, which runs script"),
            _guard.GuardPage(unknown, "page", catalog: null).Errors);
        Assert.Equal(
            new[] { "link", "image", "buttons[0].url" }.Select(x => $"page: 'content[0].{x}' holds a {scheme}: URL, which runs script").Order(),
            _guard.GuardPage(known, "page", Catalog).Errors.Order());
    }

    [Theory]
    [InlineData("JavaScript: tips")]
    [InlineData("javascript: is a language")]
    [InlineData("VBScript: a history")]
    [InlineData("javascript:alert(1)")]
    public void GuardPage_TextThatIsNoLink_IsAccepted(string value)
    {
        var page = Page(new JObject
        {
            ["id"] = "a",
            ["type"] = "hero",
            ["title"] = value,
            ["body"] = value,
            ["text"] = new JObject { ["markdown"] = value, ["html"] = $"<p>{value}</p>" },
            ["buttons"] = new JArray(new JObject { ["url"] = "/ok", ["label"] = value }),
            ["imageDescription"] = value,
            ["description"] = new JObject { ["en-US"] = value },
        });

        Assert.Empty(_guard.GuardPage(page, "page", catalog: null).Errors);
        Assert.Empty(_guard.GuardPage(page, "page", Catalog).Errors);
    }

    [Theory]
    [InlineData("/catalog/bolts")]
    [InlineData("#faq")]
    [InlineData("?q=javascript:alert(1)")]
    [InlineData("./javascript:alert(1)")]
    [InlineData("mailto:sales@example.com")]
    [InlineData("tel:+15550100")]
    [InlineData("sms:+15550100")]
    [InlineData("https://example.com/javascript:alert(1)")]
    [InlineData("http://example.com")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("javascript")]
    [InlineData("Read about JavaScript and VBScript")]
    [InlineData("<a href=\"/x\">x</a>")]
    [InlineData("Tom &amp; Jerry")]
    public void GuardPage_SafeValues_ReturnsNoErrors(string value)
    {
        var page = Page(new JObject { ["id"] = "a", ["type"] = "hero", ["title"] = value, ["deep"] = new JArray(value) });

        Assert.Empty(_guard.GuardPage(page, "page", catalog: null).Errors);
        Assert.Empty(_guard.GuardPage(page, "page", Catalog).Errors);
    }

    [Fact]
    public void GuardPage_PageSettings_AreCheckedToo()
    {
        var page = JObject.Parse("""{ "settings": { "canonicalUrl": "javascript:alert(1)" }, "content": [] }""");

        Assert.Equal(["page: 'settings.canonicalUrl' holds a javascript: URL, which runs script"], _guard.GuardPage(page, "page", null).Errors);
    }

    [Fact]
    public void GuardPage_LegacyArray_ChecksItsSections()
    {
        var page = JArray.Parse("""[ { "type": "settings" }, { "id": "a", "type": "hero", "body": "<p>a</p><script>1</script>", "link": "vbscript:x" } ]""");

        var result = _guard.GuardPage(page, "page", Catalog);

        Assert.Equal(["page: '[1].link' holds a vbscript: URL, which runs script"], result.Errors);
        Assert.Equal("<p>a</p>", page[1]!.Value<string>("body"));
    }

    [Theory]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("DATA:text/html,<script>alert(1)</script>")]
    [InlineData(" data:image/svg+xml,<svg onload=alert(1)>")]
    public void GuardPage_DataUrlInLinkFields_ReturnsAnError(string value)
    {
        var page = Page(new JObject
        {
            ["id"] = "a",
            ["type"] = "hero",
            ["link"] = value,
            ["buttons"] = new JArray(new JObject { ["url"] = value, ["label"] = value }),
            ["image"] = value,
        });

        var result = _guard.GuardPage(page, "page", Catalog);

        Assert.Equal(["page: 'content[0].link' links to a data: URL", "page: 'content[0].buttons[0].url' links to a data: URL"], result.Errors);
        // without a schema a link is not told from a text
        Assert.Empty(_guard.GuardPage(page, "page", catalog: null).Errors);
    }

    [Fact]
    public void GuardPage_HtmlFieldsOfTheCatalog_AreSanitizedInPlace()
    {
        var page = Page(new JObject
        {
            ["id"] = "a",
            ["type"] = "hero",
            ["body"] = "<p>Body</p><img src=x onerror=alert(1)>",
            ["raw"] = "<iframe srcdoc=\"<script>alert(1)</script>\"></iframe><p>Raw</p>",
            ["text"] = new JObject { ["markdown"] = "**b**<script>x</script>", ["html"] = "<p><strong>b</strong></p><script>x</script>" },
            ["summary"] = new JObject { ["en-US"] = "<p onclick=\"x\">en</p>", ["de-DE"] = "<p>de</p>" },
            ["source"] = "Plain <script>kept as markdown</script>",
            ["title"] = "<b onclick=\"x\">a title is text</b>",
            ["blocks"] = new JArray(new JObject { ["id"] = "c", ["type"] = "card", ["caption"] = "<a href=\"javascript:x\">c</a>" }),
        });

        var result = _guard.GuardPage(page, "page", Catalog);

        Assert.Empty(result.Errors);
        Assert.True(result.Changed);
        var hero = page["content"]![0]!;
        Assert.Equal("<p>Body</p><img src=\"x\">", hero.Value<string>("body"));
        Assert.Equal("<p>Raw</p>", hero.Value<string>("raw"));
        Assert.Equal("**b**<script>x</script>", hero["text"]!.Value<string>("markdown"));
        Assert.Equal("<p><strong>b</strong></p>", hero["text"]!.Value<string>("html"));
        Assert.Equal("<p>en</p>", hero["summary"]!.Value<string>("en-US"));
        Assert.Equal("<p>de</p>", hero["summary"]!.Value<string>("de-DE"));
        Assert.Equal("Plain <script>kept as markdown</script>", hero.Value<string>("source"));
        Assert.Equal("<b onclick=\"x\">a title is text</b>", hero.Value<string>("title"));
        Assert.Equal("<a>c</a>", hero["blocks"]![0]!.Value<string>("caption"));
    }

    [Fact]
    public void GuardPage_WithoutCatalog_SanitizesOnlyTheHtmlOfMarkdownValues()
    {
        var page = Page(new JObject
        {
            ["id"] = "a",
            ["type"] = "hero",
            ["body"] = "<p>Body</p><script>1</script>",
            ["text"] = new JObject { ["markdown"] = null, ["html"] = "<p>t</p><script>1</script>" },
        });

        var result = _guard.GuardPage(page, "page", catalog: null);

        Assert.True(result.Changed);
        Assert.Equal("<p>Body</p><script>1</script>", page["content"]![0]!.Value<string>("body"));
        Assert.Equal("<p>t</p>", page["content"]![0]!["text"]!.Value<string>("html"));
    }

    [Fact]
    public void GuardPage_LegitimateContent_IsLeftAsItIs()
    {
        var page = Page(new JObject
        {
            ["id"] = "a",
            ["type"] = "hero",
            ["link"] = "mailto:sales@example.com",
            ["image"] = "data:image/png;base64,iVBORw0KGgo=",
            ["body"] = "<h2 id=\"faq\">FAQ</h2><p><a href=\"#faq\">Up</a> <a href=\"https://x.test\" target=\"_blank\" rel=\"noopener noreferrer\">x</a></p>",
            ["text"] = new JObject { ["markdown"] = "# Hi", ["html"] = "<h1 id=\"hi\">Hi</h1>" },
            ["buttons"] = new JArray(new JObject { ["url"] = "tel:+15550100", ["label"] = "Call" }, new JObject { ["url"] = "sms:+15550100", ["label"] = "Text" }),
        });
        var before = page.DeepClone();

        var result = _guard.GuardPage(page, "page", Catalog);

        Assert.Empty(result.Errors);
        Assert.False(result.Changed);
        Assert.True(JToken.DeepEquals(before, page));
    }

    [Theory]
    [InlineData("#fff")]
    [InlineData("#FFFA")]
    [InlineData("#a1b2c3")]
    [InlineData("#A1B2C3D4")]
    [InlineData("rgb(1, 2, 3)")]
    [InlineData("rgba(1,2,3,0.5)")]
    [InlineData("rgb(10%, 20%, 30%)")]
    [InlineData("rgb(1 2 3)")]
    [InlineData("rgb(1 2 3 / 50%)")]
    [InlineData("RGBA(1 2 3 / .5)")]
    [InlineData("hsl(120, 50%, 50%)")]
    [InlineData("hsla(120deg, 50%, 50%, 0.3)")]
    [InlineData("hsl(0.5turn 10% 10%)")]
    [InlineData("hsl(-30 10% 10% / 1)")]
    public void IsColor_CssColors_ReturnsTrue(string value)
    {
        Assert.True(ThemePaletteValues.IsColor(value));
    }

    [Theory]
    [InlineData("red;}</style><script>alert(1)</script>")]
    [InlineData("red;}</style>")]
    [InlineData("url(javascript:alert(1))")]
    [InlineData("expression(alert(1))")]
    [InlineData("var(--x)")]
    [InlineData("red")]
    [InlineData("transparent")]
    [InlineData("#ggg")]
    [InlineData("#12345")]
    [InlineData("#fff;")]
    [InlineData("#fff\n")]
    [InlineData(" #fff")]
    [InlineData("rgb(1, 2)")]
    [InlineData("rgb(1, 2, 3); color: red")]
    [InlineData("rgb(1 2 3) url(x)")]
    [InlineData("hsl(1, 2%, 3%)}")]
    [InlineData("rgb(١, 2, 3)")]
    [InlineData("")]
    [InlineData(null)]
    public void IsColor_AnythingElse_ReturnsFalse(string value)
    {
        Assert.False(ThemePaletteValues.IsColor(value));
    }

    [Fact]
    public void GuardThemeFile_Palette_ChecksTokensAgainstTheCatalogElseTheirShape()
    {
        var palette = JObject.Parse("""{ "color_primary_500": "#fff", "color_dark_only": "#000", "color_other": "#111", "Bad Name": "#222", "color_x": "red;}" }""");

        var withCatalog = _guard.GuardThemeFile("config/palette.json", palette, Catalog);
        var withoutCatalog = _guard.GuardThemeFile("config/palette.dark.json", palette, null);

        Assert.Equal(
            [
                "config/palette.json: 'color_other' is not a palette token the storefront has",
                "config/palette.json: 'Bad Name' is not a palette token the storefront has",
                "config/palette.json: 'color_x' is not a palette token the storefront has",
                "config/palette.json: 'color_x' is not a CSS color",
            ],
            withCatalog.Errors);
        Assert.Equal(
            [
                "config/palette.dark.json: 'Bad Name' is not a palette token the storefront has",
                "config/palette.dark.json: 'color_x' is not a CSS color",
            ],
            withoutCatalog.Errors);
    }

    [Fact]
    public void GuardThemeFile_SectionGroupAndBrand_UseTheSchemaAndCheckEveryString()
    {
        var header = JObject.Parse("""{ "content": [ { "id": "h", "type": "hero", "body": "<p>x</p><script>1</script>", "link": "data:text/html,x" } ] }""");
        var brand = JObject.Parse("""{ "logoUrl": "javascript:alert(1)", "faviconUrl": "data:image/png;base64,AAAA" }""");

        var headerResult = _guard.GuardThemeFile("sections/header-group.json", header, Catalog);
        var brandResult = _guard.GuardThemeFile("config/brand.json", brand, Catalog);

        Assert.Equal(["sections/header-group.json: 'content[0].link' links to a data: URL"], headerResult.Errors);
        Assert.Equal("<p>x</p>", header["content"]![0]!.Value<string>("body"));
        Assert.Equal(["config/brand.json: 'logoUrl' holds a javascript: URL, which runs script"], brandResult.Errors);
    }

    [Fact]
    public void GuardContent_Sanitized_IsWrittenAgainInItsLayout()
    {
        const string compact = """{"settings":{},"content":[{"type":"t","text":{"markdown":"x","html":"<p>x</p><script>1</script>"}}]}""";
        var errors = new System.Collections.Generic.List<string>();

        var saved = _guard.GuardPageContent(compact, "page", null, errors);
        var indented = _guard.GuardPageContent(JToken.Parse(compact).ToString(), "page", null, errors);

        Assert.Empty(errors);
        Assert.Equal("""{"settings":{},"content":[{"type":"t","text":{"markdown":"x","html":"<p>x</p>"}}]}""", saved);
        Assert.Contains("\n", indented);
        Assert.Same(saved, _guard.GuardPageContent(saved, "page", null, errors));
        Assert.Equal("not json", _guard.GuardPageContent("not json", "page", null, errors));
    }

    private static JObject Page(params JObject[] items) =>
        new() { ["settings"] = new JObject(), ["content"] = new JArray(items.Cast<object>().ToArray()) };
}
