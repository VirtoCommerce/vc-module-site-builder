using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using VirtoCommerce.AssetsModule.Core.Assets;
using VirtoCommerce.SiteBuilder.Data.Theme;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class CustomSectionValidatorTests
{
    private const string Type = "custom-hero";
    private const string File = "custom-sections/custom-hero.json";
    private const string Assets = "https://cdn.example/assets/";

    private const string Settings = """
        [
          { "type": "header", "label": "Content" },
          { "id": "title", "type": "string", "translatable": true, "label": "Title" },
          { "id": "body", "type": "text", "translatable": true },
          { "id": "link", "type": "string", "format": "url" },
          { "id": "image", "type": "images" },
          { "id": "count", "type": "number", "min": 0, "max": 1000 },
          { "id": "ends", "type": "calendar" },
          { "id": "tone", "type": "color-token" },
          { "id": "show", "type": "checkbox" },
          { "id": "view", "type": "select", "options": [ { "value": "grid", "label": "Grid" }, { "value": "carousel" } ] },
          { "id": "items", "type": "list", "element": [
            { "id": "title", "type": "string" }, { "id": "image", "type": "images" }, { "id": "link", "type": "string", "format": "url" }, { "id": "body", "type": "text" }
          ] },
          { "id": "cta", "type": "object", "element": [ { "id": "label", "type": "string" } ] }
        ]
        """;

    private const string ValidTemplate = """
        <section class="hero {{ settings.view }}" data-inview data-inview-threshold="0.3" data-inview-once data-pointer>
          <h2 data-if="settings.title">{{ settings.title }}</h2>
          <div class="hero__body" data-html="settings.body"></div>
          <img src="{{ settings.image }}" alt="{{ settings.title }}" loading="lazy">
          <img src="https://cdn.example/assets/stores/s1/hero.png" srcset="https://cdn.example/assets/a.png 1x, https://cdn.example/assets/b.png 2x" alt="">
          <ul class="hero__items" data-marquee>
            <li data-each="settings.items" data-if="!item.title" aria-label="{{ item.title }}">
              <a href="{{ item.link }}"><img src="{{ item.image }}" alt=""></a>
              <div data-html="item.body"></div>
            </li>
          </ul>
          <p><span data-count-up="{{ settings.count }}" data-count-up-duration="1500" data-count-up-decimals="0">0</span>+</p>
          <p data-countdown="{{ settings.ends }}"><span data-countdown-part="days"></span>:<span data-countdown-part="hours"></span></p>
          <p data-countdown="2026-12-31T23:59:59Z" data-copy="{{ settings.cta.label }}" data-scroll-progress>{{ settings.cta.label }}</p>
          <a href="/catalog" target="_blank" rel="noopener">{{ settings.title }}</a>
          <a href="mailto:sales@example.com">{{ settings.title }}</a>
          <a href="{{ settings.link }}" title="{{ settings.title }}">{{ settings.title }}</a>
          <table><tr><td colspan="2" scope="row">{{ settings.title }}</td></tr></table>
          <vc-button label="{{ settings.cta.label }}" to="{{ settings.link }}" color="primary" variant="outline" size="lg" append-icon="arrow-right" full-width data-if="settings.show"></vc-button>
          <vc-icon name="lucide-megaphone" size="24" color="accent"></vc-icon>
          <vc-badge label="{{ settings.title }}" color="success" variant="soft" size="sm"></vc-badge>
          <vc-products query="shoes" filter="price:[0 TO 100]" take="8" view="{{ settings.view }}" columns="4"></vc-products>
          <!-- a comment -->
        </section>
        """;

    private const string ValidCss = """
        :scope { color: var(--color-neutral-900); background: var(--setting-image) center / cover; position: relative; }
        :scope[data-visible] .hero__items { opacity: 1; transform: translateY(calc(var(--scroll-progress) * 10px)); }
        .hero__items > li { animation: custom-hero-fade 1s calc(var(--i) * 100ms) both; border-color: transparent; color: currentColor; }
        .hero__body { background-image: url("https://cdn.example/assets/stores/s1/texture.png"); }
        .hero__button { & .icon { opacity: .5; } }
        @media (width >= 600px) { .hero__items { display: grid; grid-template-columns: repeat(var(--n), 1fr); } }
        @supports (display: grid) { .hero__items { gap: 1rem; } }
        @container (min-width: 400px) { .hero__body { font-size: 1.25rem; } }
        @keyframes custom-hero-fade { from { opacity: 0; } to { opacity: 1; } }
        @media (prefers-reduced-motion: reduce) { .hero__items > li { animation: none; } }
        """;

    // the built-in kit tags and behaviors, as the storefront announces them in its catalog
    private static readonly JObject Catalog = JObject.Parse("""
        {
          "sections": {},
          "kit": {
            "tags": {
              "vc-button": {
                "description": "A button or a link",
                "attributes": {
                  "label": { "type": "string" },
                  "to": { "type": "link" },
                  "color": { "type": "select", "options": [ "primary", "secondary", "neutral", "accent", "info", "success", "warning", "danger" ] },
                  "variant": { "type": "select", "options": [ "solid", "outline", "soft", "surface", "ghost", "tonal" ] },
                  "size": { "type": "select", "options": [ "xxs", "xs", "sm", "md", "lg" ] },
                  "prepend-icon": { "type": "string" },
                  "append-icon": { "type": "string" },
                  "full-width": { "type": "boolean" }
                }
              },
              "vc-icon": { "attributes": { "name": { "type": "string" }, "size": { "type": "string" }, "color": { "type": "select", "options": [ "primary", "accent" ] } } },
              "vc-badge": {
                "attributes": {
                  "label": { "type": "string" },
                  "color": { "type": "select", "options": [ "primary", "success" ] },
                  "variant": { "type": "select", "options": [ { "value": "solid", "label": "Solid" }, { "value": "soft" }, { "value": "outline" } ] },
                  "size": { "type": "select", "options": [ "xs", "sm", "md", "lg" ] }
                }
              },
              "vc-products": {
                "attributes": {
                  "query": { "type": "string" },
                  "filter": { "type": "string" },
                  "take": { "type": "number", "min": 1, "max": 24 },
                  "view": { "type": "select", "options": [ "grid", "carousel" ] },
                  "columns": { "type": "number", "min": 1, "max": 6 }
                }
              },
              "rating": { "attributes": {} }
            },
            "behaviors": {
              "inview": { "options": { "threshold": { "type": "number", "min": 0, "max": 1 }, "once": { "type": "boolean" } }, "writes": [ "data-visible" ] },
              "scroll-progress": { "options": {} },
              "pointer": { "options": {} },
              "count-up": {
                "value": { "type": "number" },
                "options": { "duration": { "type": "number", "min": 0, "max": 5000 }, "decimals": { "type": "number", "min": 0, "max": 4 } }
              },
              "countdown": { "value": { "type": "date" }, "options": { "part": { "type": "select", "options": [ "days", "hours", "minutes", "seconds" ] } } },
              "copy": { "options": {} },
              "marquee": { "options": {} }
            }
          }
        }
        """);

    private readonly CustomSectionValidator _validator = new(new AssetUrls(Assets));

    [Fact]
    public void Validate_TypeWithinTheRules_HasNoErrorsAndNoWarnings()
    {
        var result = _validator.Validate(Type, Section(ValidTemplate, ValidCss), Catalog);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("""<section><svg><script>alert(1)</script></svg></section>""", "<svg> at line 1 is not an element a custom section can hold")]
    [InlineData("""<section><script>alert(1)</script></section>""", "<script> at line 1 is not an element a custom section can hold")]
    [InlineData("""<section><iframe src="https://cdn.example/assets/a.html"></iframe></section>""", "<iframe> at line 1 is not an element a custom section can hold")]
    [InlineData("""<section><input name="q"></section>""", "<input> at line 1 is not an element a custom section can hold")]
    [InlineData("""<section><button>Go</button></section>""", "<button> at line 1 is not an element a custom section can hold")]
    [InlineData("""<section id="hero"></section>""", "<section> at line 1 has an attribute a custom section cannot use, 'id': two sections on a page would share it")]
    [InlineData("""<section style="color: red"></section>""", "<section> at line 1 has an attribute a custom section cannot use, 'style': the section's CSS styles it")]
    [InlineData("""<section onclick="go()"></section>""", "<section> at line 1 has an attribute a custom section cannot use, 'onclick': it runs script")]
    [InlineData("""<section data-visible></section>""", "<section> at line 1 has an attribute a custom section cannot use, 'data-visible': it is no binding and no behavior the storefront offers")]
    [InlineData("""<section></section><div></div>""", "the template has 2 root elements, and has one: the section's root")]
    [InlineData("""text <section></section>""", "the template has text outside its root element")]
    [InlineData("""<section data-each="settings.items"></section>""", "<section> at line 1 is the root and has data-each, which repeats an element inside the root")]
    [InlineData("""<section><ul data-each="settings.items"><li data-each="settings.items"></li></ul></section>""", "<li> at line 1 has data-each inside an element that has one")]
    [InlineData("""<section><ul><li data-each="settings.title"></li></ul></section>""", "<li> at line 1 repeats for 'title', a string and not a list")]
    [InlineData("""<section><ul><li data-each="item.title"></li></ul></section>""", "<li> at line 1 has data-each=\"item.title\", which is not settings.<id> of a list setting")]
    [InlineData("""<section><p>{{ settings.price }}</p></section>""", "<p> at line 1 binds 'settings.price', a setting the schema does not declare")]
    [InlineData("""<section><p>{{ item.title }}</p></section>""", "<p> at line 1 binds 'item.title' outside an element with data-each, where there is no item")]
    [InlineData("""<section><ul><li data-each="settings.items">{{ item.price }}</li></ul></section>""", "<li> at line 1 binds 'item.price', a field the rows of 'items' do not have")]
    [InlineData("""<section><p>{{ settings.items }}</p></section>""", "<p> at line 1 binds 'settings.items', a list, which data-each repeats an element for")]
    [InlineData("""<section><p>{{ settings.cta }}</p></section>""", "<p> at line 1 binds 'settings.cta', an object, whose fields are bound one by one")]
    [InlineData("""<section><p>{{ settings.title.text }}</p></section>""", "<p> at line 1 binds 'settings.title.text', but 'title' is a string and not an object")]
    [InlineData("""<section><p>{{ settings.cta.color }}</p></section>""", "<p> at line 1 binds 'settings.cta.color', a field 'cta' does not have")]
    [InlineData("""<section><p>{{ settings.title | upcase }}</p></section>""", "<p> at line 1 binds 'settings.title | upcase', which is not settings.<id>, settings.<id>.<field> or item.<field>")]
    [InlineData("""<section><p>{{ settings.title</p></section>""", "<p> at line 1 has a '{{' or a '}}' that is no binding, such as {{ settings.title }}")]
    [InlineData("""<section><p data-if="settings.price"></p></section>""", "<p> at line 1 binds 'settings.price', a setting the schema does not declare")]
    [InlineData("""<section><div data-html="settings.title"></div></section>""", "<div> at line 1 has data-html=\"settings.title\", a string; it names a text setting, whose value is HTML")]
    [InlineData("""<section><div data-html="settings.body"><b>x</b></div></section>""", "<div> at line 1 has data-html and content of its own")]
    [InlineData("""<section><a href="javascript:alert(1)">x</a></section>""", "<a> at line 1 has href=\"javascript:alert(1)\", which is not a path starting with '/', or an https:, mailto: or tel: URL")]
    [InlineData("""<section><a href="//evil.example/x">x</a></section>""", "<a> at line 1 has href=\"//evil.example/x\", which is not a path starting with '/', or an https:, mailto: or tel: URL")]
    [InlineData("""<section><a href="http://example.com">x</a></section>""", "<a> at line 1 has href=\"http://example.com\", which is not a path starting with '/', or an https:, mailto: or tel: URL")]
    [InlineData("""<section><a href="{{ settings.title }}">x</a></section>""", "<a> at line 1 has href bound to 'settings.title', which is not a link setting: a string with \"format\": \"url\"")]
    [InlineData("""<section><a href="/p/{{ settings.title }}">x</a></section>""", "<a> at line 1 has href=\"/p/{{ settings.title }}\", which is a link or one binding of a link setting")]
    [InlineData("""<section><img src="https://evil.example/x.png"></section>""", "<img> at line 1 has src with 'https://evil.example/x.png', which is not a URL of the store's asset library")]
    [InlineData("""<section><img src="/assets/x.png"></section>""", "<img> at line 1 has src with '/assets/x.png', which is not a URL of the store's asset library")]
    [InlineData("""<section><img src="data:image/png;base64,AAAA"></section>""", "<img> at line 1 has src with 'data:image/png;base64,AAAA', which is not a URL of the store's asset library")]
    [InlineData("""<section><img src="{{ settings.link }}"></section>""", "<img> at line 1 has src bound to 'settings.link', a string and not an images setting")]
    [InlineData("""<section><img srcset="https://cdn.example/assets/a.png 1x, https://evil.example/b.png 2x"></section>""", "<img> at line 1 has srcset with 'https://evil.example/b.png', which is not a URL of the store's asset library")]
    [InlineData("""<section><vc-button color="pink"></vc-button></section>""", "<vc-button> at line 1 has color=\"pink\", which vc-button does not take: it takes primary, secondary, neutral, accent, info, success, warning, danger")]
    [InlineData("""<section><vc-button variant="solid-light"></vc-button></section>""", "<vc-button> at line 1 has variant=\"solid-light\", which vc-button does not take: it takes solid, outline, soft, surface, ghost, tonal")]
    [InlineData("""<section><vc-button full-width="yes"></vc-button></section>""", "<vc-button> at line 1 has full-width=\"yes\", which vc-button does not take: it takes true, false or no value")]
    [InlineData("""<section><vc-button onclick="go()"></vc-button></section>""", "<vc-button> at line 1 has an attribute vc-button does not take, 'onclick'")]
    [InlineData("""<section><vc-button class="cta"></vc-button></section>""", "<vc-button> at line 1 has an attribute vc-button does not take, 'class'")]
    [InlineData("""<section><vc-button data-copy="x"></vc-button></section>""", "<vc-button> at line 1 has an attribute vc-button does not take, 'data-copy'")]
    [InlineData("""<section><vc-carousel></vc-carousel></section>""", "<vc-carousel> at line 1 is not a kit tag the storefront offers")]
    [InlineData("""<section><rating></rating></section>""", "<rating> at line 1 is not an element a custom section can hold")]
    [InlineData("""<section><vc-button to="javascript:go()"></vc-button></section>""", "<vc-button> at line 1 has to=\"javascript:go()\", which is not a path starting with '/', or an https:, mailto: or tel: URL")]
    [InlineData("""<section><vc-button>Go</vc-button></section>""", "<vc-button> at line 1 has content, which a kit tag does not have")]
    [InlineData("""<section><vc-badge variant="tonal"></vc-badge></section>""", "<vc-badge> at line 1 has variant=\"tonal\", which vc-badge does not take: it takes solid, soft, outline")]
    [InlineData("""<section><vc-products take="30"></vc-products></section>""", "<vc-products> at line 1 has take=\"30\", which vc-products does not take: it takes a number from 1 to 24")]
    [InlineData("""<section><vc-products take="many"></vc-products></section>""", "<vc-products> at line 1 has take=\"many\", which vc-products does not take: it takes a number from 1 to 24")]
    [InlineData("""<section><vc-products view="list"></vc-products></section>""", "<vc-products> at line 1 has view=\"list\", which vc-products does not take: it takes grid, carousel")]
    [InlineData("""<section><vc-products columns="{{ settings.count }} cols"></vc-products></section>""", "<vc-products> at line 1 has columns=\"{{ settings.count }} cols\", which is a value or one binding")]
    [InlineData("""<section><vc-products take="{{ settings.price }}"></vc-products></section>""", "<vc-products> at line 1 binds 'settings.price', a setting the schema does not declare")]
    [InlineData("""<section><vc-products data-html="settings.body"></vc-products></section>""", "<vc-products> at line 1 has data-html, and a kit tag holds nothing")]
    [InlineData("""<section><p data-inview-threshold="0.5"></p></section>""", "<p> at line 1 has data-inview-threshold, an option of data-inview, outside an element with data-inview")]
    [InlineData("""<section><p data-inview data-inview-threshold="2"></p></section>""", "<p> at line 1 has data-inview-threshold=\"2\", which data-inview does not take: it takes a number from 0 to 1")]
    [InlineData("""<section><p data-inview data-inview-once="maybe"></p></section>""", "<p> at line 1 has data-inview-once=\"maybe\", which data-inview does not take: it takes true, false or no value")]
    [InlineData("""<section><p data-inview data-inview-delay="1"></p></section>""", "<p> at line 1 has data-inview-delay, which is not an option data-inview takes")]
    [InlineData("""<section><span data-count-up="5" data-count-up-duration="9000"></span></section>""", "<span> at line 1 has data-count-up-duration=\"9000\", which data-count-up does not take: it takes a number from 0 to 5000")]
    [InlineData("""<section><span data-count-up="{{ settings.price }}"></span></section>""", "<span> at line 1 binds 'settings.price', a setting the schema does not declare")]
    [InlineData("""<section><span data-count-up="many"></span></section>""", "<span> at line 1 has data-count-up=\"many\", which data-count-up does not take: it takes a number")]
    [InlineData("""<section><span data-count-up="{{ settings.count }}+"></span></section>""", "<span> at line 1 has data-count-up=\"{{ settings.count }}+\", which is a value or one binding")]
    [InlineData("""<section><span data-countdown="tomorrow"></span></section>""", "<span> at line 1 has data-countdown=\"tomorrow\", which data-countdown does not take: it takes an ISO 8601 date, or a date and a time, such as 2026-12-31 or 2026-12-31T23:59:59Z")]
    [InlineData("""<section><span data-countdown="2026-02-30"></span></section>""", "<span> at line 1 has data-countdown=\"2026-02-30\", which data-countdown does not take: it takes an ISO 8601 date, or a date and a time, such as 2026-12-31 or 2026-12-31T23:59:59Z")]
    [InlineData("""<section><span data-countdown="31.12.2026"></span></section>""", "<span> at line 1 has data-countdown=\"31.12.2026\", which data-countdown does not take: it takes an ISO 8601 date, or a date and a time, such as 2026-12-31 or 2026-12-31T23:59:59Z")]
    [InlineData("""<section><span data-countdown-part="days"></span></section>""", "<span> at line 1 has data-countdown-part, an option of data-countdown, outside an element with data-countdown")]
    [InlineData("""<section data-countdown="2026-12-31"><span data-countdown-part="weeks"></span></section>""", "<span> at line 1 has data-countdown-part=\"weeks\", which data-countdown does not take: it takes days, hours, minutes, seconds")]
    public void Validate_TemplateThatBreaksARule_IsRefusedWithTheRule(string template, string error)
    {
        var result = _validator.Validate(Type, Section(template), Catalog);

        Assert.Contains($"{File}: {error}", result.Errors);
    }

    [Fact]
    public void Validate_KitWithoutACatalog_IsRefusedAndTheRestIsChecked()
    {
        var result = _validator.Validate(Type, Section("""<section data-inview><vc-button label="{{ settings.title }}"></vc-button><p>{{ settings.title }}</p></section>"""), null);

        Assert.Equal(
            [
                $"{File}: <section> at line 1 has an attribute a custom section cannot use, 'data-inview': it is no binding, and no schema catalog names the storefront's behaviors",
                $"{File}: <vc-button> at line 1 is a kit tag, and no schema catalog names the storefront's kit tags: save it with the catalog of the storefront's preview",
            ],
            result.Errors);
        Assert.Empty(_validator.Validate(Type, Section("""<section><p>{{ settings.title }}</p></section>"""), null).Errors);
    }

    [Fact]
    public void Validate_KitOfAStorefrontThatOffersNone_IsRefused()
    {
        var result = _validator.Validate(Type, Section("""<section data-inview><vc-button></vc-button></section>"""), JObject.Parse("""{ "sections": {} }"""));

        Assert.Equal(
            [
                $"{File}: <section> at line 1 has an attribute a custom section cannot use, 'data-inview': it is no binding, and the storefront offers no behaviors, as its catalog has no kit",
                $"{File}: <vc-button> at line 1 is a kit tag, and the storefront offers none: its catalog has no kit",
            ],
            result.Errors);
    }

    [Fact]
    public void Validate_KitTagAndBehaviorAModuleAdds_AreTakenAsTheCatalogDeclaresThem()
    {
        var catalog = (JObject)Catalog.DeepClone();
        catalog["kit"]!["tags"]!["vc-rating"] = JObject.Parse("""{ "attributes": { "value": { "type": "number", "min": 0, "max": 5 }, "since": { "type": "date" } } }""");
        catalog["kit"]!["behaviors"]!["tilt"] = JObject.Parse("""{ "options": { "max": { "type": "number", "max": 45 } } }""");

        var valid = _validator.Validate(Type, Section("""<section data-tilt data-tilt-max="30"><vc-rating value="4.5" since="2026-11-21T09:30:00+02:00"></vc-rating></section>"""), catalog);
        var invalid = _validator.Validate(Type, Section("""<section data-tilt data-tilt-max="60"><vc-rating value="6" since="soon"></vc-rating></section>"""), catalog);

        Assert.Empty(valid.Errors);
        Assert.Equal(
            [
                $"{File}: <section> at line 1 has data-tilt-max=\"60\", which data-tilt does not take: it takes a number up to 45",
                $"{File}: <vc-rating> at line 1 has value=\"6\", which vc-rating does not take: it takes a number from 0 to 5",
                $"{File}: <vc-rating> at line 1 has since=\"soon\", which vc-rating does not take: it takes an ISO 8601 date, or a date and a time, such as 2026-12-31 or 2026-12-31T23:59:59Z",
            ],
            invalid.Errors);
    }

    [Theory]
    [InlineData("""<section><vc-button label="Go" /><p>x</p></section>""", "the template does not read as HTML at line 1, column 10: The given tag cannot be self-closed.")]
    [InlineData("""<section><p>a < b</p></section>""", "the template does not read as HTML at line 1, column 16: Ambiguous open tag.")]
    [InlineData("<section>\n  <div>\n</section>", "the template does not read as HTML at line 3, column 1: ")]
    public void Validate_TemplateTheParserMends_IsRefusedAtWhereItBreaks(string template, string error)
    {
        var result = _validator.Validate(Type, Section(template), Catalog);

        Assert.StartsWith($"{File}: {error}", result.Errors[0]);
    }

    [Fact]
    public void Validate_ElementOnALaterLine_IsNamedAtItsLine()
    {
        var result = _validator.Validate(Type, Section("<section>\n  <p>\n    <input>\n    <svg></svg>\n  </p>\n</section>"), Catalog);

        Assert.Equal(
            [$"{File}: <input> at line 3 is not an element a custom section can hold", $"{File}: <svg> at line 4 is not an element a custom section can hold"],
            result.Errors);
    }

    [Theory]
    [InlineData("""@import url("https://cdn.example/assets/a.css");""", "the CSS has @import, and a custom section's CSS has only @media, @supports, @container and @keyframes")]
    [InlineData("""@font-face { font-family: x; }""", "the CSS has @font-face, and a custom section's CSS has only @media, @supports, @container and @keyframes")]
    [InlineData(""".a { color: var(--color-primary-500); } @layer base { .a { margin: 0; } }""", "the CSS has @layer, and a custom section's CSS has only @media, @supports, @container and @keyframes")]
    [InlineData("""@scope (.a) { .b { margin: 0; } }""", "the CSS has @scope, and a custom section's CSS has only @media, @supports, @container and @keyframes")]
    [InlineData("""@property --x { syntax: '<length>'; inherits: false; initial-value: 0px; }""", "the CSS has @property, and a custom section's CSS has only @media, @supports, @container and @keyframes")]
    [InlineData("""@page { margin: 0; }""", "the CSS has @page, and a custom section's CSS has only @media, @supports, @container and @keyframes")]
    [InlineData("""@namespace svg url(http://www.w3.org/2000/svg);""", "the CSS has @namespace, and a custom section's CSS has only @media, @supports, @container and @keyframes")]
    [InlineData("""@media (width >= 600px) { @font-face { font-family: x; } }""", "the CSS has @font-face, and a custom section's CSS has only @media, @supports, @container and @keyframes")]
    [InlineData(""":root { --x: 1; }""", "the CSS selector ':root' reaches :root, html or body, outside the section")]
    [InlineData("""body .a { margin: 0; }""", "the CSS selector 'body .a' reaches :root, html or body, outside the section")]
    [InlineData(""".a, html { margin: 0; }""", "the CSS selector '.a, html' reaches :root, html or body, outside the section")]
    [InlineData(""".a { position: fixed; }""", "the CSS has position: fixed, which puts the section over the page")]
    [InlineData(""".a { & .b { POSITION:FIXED } }""", "the CSS has position: fixed, which puts the section over the page")]
    [InlineData(""".a { background: url(https://evil.example/x.png); }""", "the CSS loads 'https://evil.example/x.png', which is not a URL of the store's asset library")]
    [InlineData(""".a { background-image: image-set("https://evil.example/x.png" 1x); }""", "the CSS loads 'https://evil.example/x.png', which is not a URL of the store's asset library")]
    [InlineData(""".a { background: u\72l(https://evil.example/x.png); }""", "the CSS loads 'https://evil.example/x.png', which is not a URL of the store's asset library")]
    [InlineData(""".a { width: expression(alert(1)); }""", "the CSS has expression(), which runs script")]
    [InlineData(""".a { behavior: url(https://cdn.example/assets/x.htc); }""", "the CSS sets behavior, which runs script")]
    [InlineData(""".a { -moz-binding: url(https://cdn.example/assets/x.xml#y); }""", "the CSS sets -moz-binding, which runs script")]
    [InlineData("""@keyframes fade { from { opacity: 0; } }""", "the CSS names @keyframes 'fade', which does not start with the type, 'custom-hero': names are not scoped")]
    [InlineData("""} body { display: none; } .a {""", "the CSS has a '}' without its pair")]
    [InlineData(""".a { color: red; """, "the CSS has a '{' without its pair")]
    [InlineData(""".a { background: rgb(0 0 0 }""", "the CSS has a '}' without its pair")]
    [InlineData(""".a{background:url(x')}}} *{display:none!important} .b{content:'}""", "the CSS has a url( whose URL a quote, a parenthesis or a space breaks: quote the URL")]
    [InlineData(".a{content:'x\r}}} *{display:none}\n}", "the CSS has a string that a line break cuts off")]
    [InlineData(".a{content:'x\f}}} *{display:none}\n}", "the CSS has a string that a line break cuts off")]
    [InlineData(""":scope{--a:"/*";position:fixed;inset:0;z-index:2147483647;--b:"*/"}""", "the CSS has position: fixed, which puts the section over the page")]
    [InlineData(""":scope{--a:"/*";background:url(https://evil.example/t.gif);--b:"*/"}""", "the CSS loads 'https://evil.example/t.gif', which is not a URL of the store's asset library")]
    [InlineData(""".a { background: url("https://evil.example/t.gif\"") }""", "the CSS loads 'https://evil.example/t.gif\"', which is not a URL of the store's asset library")]
    [InlineData(""":scope { --u: url(https://evil.example/t.gif); }""", "the CSS loads 'https://evil.example/t.gif', which is not a URL of the store's asset library")]
    [InlineData(""":scope { --p: fixed; position: var(--p); }""", "the CSS sets position with var(), which could make it fixed")]
    [InlineData(""".a { posit\69 on: fi\78 ed; }""", "the CSS has position: fixed, which puts the section over the page")]
    [InlineData(""".a { background-image: image-set(var(--u) 1x); }""", "the CSS has var() inside image-set(), whose URL is then not known on save")]
    [InlineData("""@media a { @media b { @media c { @media d { @media e { .a { margin: 0; } } } } } }""", "the CSS nests at-rules deeper than 4")]
    public void Validate_CssThatBreaksARule_IsRefusedWithTheRule(string css, string error)
    {
        var result = _validator.Validate(Type, Section("<section></section>", css), Catalog);

        Assert.Contains($"{File}: {error}", result.Errors);
    }

    [Fact]
    public void Validate_CssNestedDeeperThanTheLimit_IsRefusedBeforeItIsWalked()
    {
        var css = string.Concat(Enumerable.Repeat(".a { ", 5000)) + string.Concat(Enumerable.Repeat("} ", 5000));

        var result = _validator.Validate(Type, Section("<section></section>", css), Catalog);

        Assert.Equal([$"{File}: the CSS nests blocks deeper than 32"], result.Errors);
    }

    [Fact]
    public void Validate_TextAndColorsOfItsOwn_AreSavedWithWarnings()
    {
        const string template = """<section><h2>Free shipping</h2><img alt="Truck" src="{{ settings.image }}"><p>{{ settings.count }} %</p></section>""";
        const string css = """.a { color: #ff0000; background: rgb(0 0 0 / 50%); border-color: var(--color-primary-500); }""";

        var result = _validator.Validate(Type, Section(template, css), Catalog);

        Assert.Empty(result.Errors);
        Assert.Equal(
            [
                $"{File}: the template holds text of its own ('Free shipping', 'Truck'), which is the same in every language: bind it to a translatable setting",
                $"{File}: the CSS sets literal colors (color, background-color), which the palette does not change: use var(--color-…) or a color-token setting's variable",
            ],
            result.Warnings);
    }

    [Theory]
    [InlineData("""[ { "id": "x", "type": "menu" } ]""", "setting 'x' has a type a custom section does not take, 'menu'")]
    [InlineData("""[ { "id": "x", "type": "string", "visibility": "this.model.y" } ]""", "setting 'x' has a key a custom section's setting does not take, 'visibility'")]
    [InlineData("""[ { "id": "x", "type": "select", "options": [ { "value": "a" } ], "request": { "url": "https://evil.example" } } ]""", "setting 'x' has a key a custom section's setting does not take, 'request'")]
    [InlineData("""[ { "id": "x", "type": "select" } ]""", "setting 'x' is a select without a list of options, each with a value")]
    [InlineData("""[ { "id": "x", "type": "string", "options": [ { "value": "a" } ] } ]""", "setting 'x' has options, which only a select has")]
    [InlineData("""[ { "id": "x", "type": "number", "translatable": true } ]""", "setting 'x' is translatable, which only a string or a text is")]
    [InlineData("""[ { "id": "x", "type": "string", "format": "email" } ]""", "setting 'x' has a format other than the \"url\" of a string that holds a link")]
    [InlineData("""[ { "id": "x", "type": "images", "multiple": true } ]""", "setting 'x' is multiple, and a custom section's setting holds one value")]
    [InlineData("""[ { "id": "x", "type": "list" } ]""", "setting 'x' is a list without the fields of its rows under 'element'")]
    [InlineData("""[ { "id": "x", "type": "string", "element": [ { "id": "y", "type": "string" } ] } ]""", "setting 'x' has an element, which only a list or an object has")]
    [InlineData("""[ { "id": "x", "type": "list", "element": [ { "id": "y", "type": "list", "element": [ { "id": "z", "type": "string" } ] } ] } ]""",
        "setting 'x.y' is a list inside 'x', one level deeper than a custom section's settings go")]
    [InlineData("""[ { "id": "x-y", "type": "string" } ]""", "setting 'x-y' has no id of letters, digits and '_' that starts with a letter")]
    [InlineData("""[ { "type": "string" } ]""", "setting 1 has no id of letters, digits and '_' that starts with a letter")]
    [InlineData("""[ { "id": "x", "type": "string" }, { "id": "x", "type": "number" } ]""", "setting 'x' has the id of another setting")]
    [InlineData("""[ "x" ]""", "setting 1 is not an object")]
    public void Validate_SettingThatBreaksARule_IsRefusedWithTheRule(string settings, string error)
    {
        var result = _validator.Validate(Type, Section("<section></section>", settings: settings), Catalog);

        Assert.Contains($"{File}: {error}", result.Errors);
    }

    [Fact]
    public void Validate_FileOfAnotherShape_IsRefusedForEachKey()
    {
        var file = Section("<section></section>");
        file["format"] = 2;
        file["name"] = new JObject { ["en-US"] = "" };
        file["script"] = "alert(1)";
        file["css"] = 1;
        file["schema"]!["limit"] = 0;
        file["schema"]!["targetTemplates"] = "product";
        file["schema"]!["blocks"] = new JArray("text");

        var result = _validator.Validate(Type, file, Catalog);

        Assert.Equal(
            [
                $"{File}: 'script' is not a key of a custom section type, which has format, name, schema, template and css",
                $"{File}: 'format' is not 1",
                $"{File}: 'name' is not a text, or a text per language",
                $"{File}: 'schema.blocks' is not a key of a custom section's schema, which has settings, targetTemplates, limit and icon",
                $"{File}: 'schema.targetTemplates' is not a list of template names",
                $"{File}: 'schema.limit' is not a whole number from 1",
                $"{File}: 'css' is not a text",
            ],
            result.Errors);
    }

    [Fact]
    public void Validate_TemplateOverItsSize_IsRefusedWithoutReadingIt()
    {
        var result = _validator.Validate(Type, Section($"<section><p>{new string('x', 70 * 1024)}</p></section>"), Catalog);

        Assert.Equal([$"{File}: 'template' is 70 KB, more than 64 KB"], result.Errors);
    }

    [Theory]
    [InlineData("Custom-Hero")]
    [InlineData("hero")]
    [InlineData("custom-")]
    [InlineData("custom-hero_banner")]
    [InlineData("custom-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Validate_FileNamedAsNoType_IsRefused(string type)
    {
        var result = _validator.Validate(type, Section("<section></section>"), Catalog);

        Assert.Equal(
            [$"custom-sections/{type}.json: '{type}' is not a custom section type, which is 'custom-' and lowercase words joined by '-', at most 64 characters"],
            result.Errors);
    }

    [Fact]
    public void Validate_WithoutAnAssetLibraryAddress_RefusesALiteralImageButNotABoundOne()
    {
        var result = new CustomSectionValidator().Validate(Type, Section("""<section><img src="https://cdn.example/assets/a.png"><img src="{{ settings.image }}"></section>"""), Catalog);

        Assert.Equal([$"{File}: <img> at line 1 has src with 'https://cdn.example/assets/a.png', which is not a URL of the store's asset library"], result.Errors);
    }

    [Fact]
    public void GetSizeError_TypesTogetherOverTheLimit_NamesTheirSizeAndTheLimit()
    {
        var padding = $"/* {new string('x', 60 * 1024)} */";
        var types = Enumerable.Range(1, 5).ToDictionary(x => $"custom-hero-{x}", _ => (JToken)Section("<section></section>", padding));

        var error = _validator.GetSizeError(types);
        types.Remove("custom-hero-5");

        Assert.Equal("custom-sections: the custom section types are 304.2 KB of JSON together, more than 256 KB: the snapshot carries every type to every page", error);
        Assert.Null(_validator.GetSizeError(types));
    }

    private static JObject Section(string template, string css = "", string settings = Settings)
    {
        var file = JObject.Parse($$"""{ "format": 1, "name": { "en-US": "Hero", "de-DE": "Held" }, "schema": { "settings": {{settings}}, "limit": 2, "icon": "star" } }""");
        file["template"] = template;
        file["css"] = css;

        return file;
    }

    private sealed class AssetUrls(string publicUrl) : IBlobUrlResolver
    {
        public string GetAbsoluteUrl(string blobKey) => publicUrl + blobKey.TrimStart('/');
    }
}
