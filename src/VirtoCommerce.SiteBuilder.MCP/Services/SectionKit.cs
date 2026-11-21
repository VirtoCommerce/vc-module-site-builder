using System.Text.Json.Nodes;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

// What an agent needs to write a custom section type that create_section_type and change_section_type take: the lists come from the validator and the
// storefront's catalog, the rest is the contract in prose (docs/contracts/custom-sections.md).
public static class SectionKit
{
    public const string ExampleType = "custom-stats-band";

    public const string ExampleFile = """
        {
          "format": 1,
          "name": "Stats band",
          "schema": {
            "settings": [
              { "id": "heading", "type": "string", "label": "Heading", "translatable": true, "default": "Why customers choose us" },
              { "id": "background", "type": "color-token", "label": "Background", "default": "color_primary_50" },
              {
                "id": "stats",
                "type": "list",
                "label": "Numbers",
                "element": [
                  { "id": "value", "type": "number", "label": "Number" },
                  { "id": "suffix", "type": "string", "label": "After the number" },
                  { "id": "label", "type": "string", "label": "Label", "translatable": true }
                ]
              }
            ]
          },
          "template": "<section class=\"stats\" data-inview data-inview-once>\n  <h2 class=\"stats__heading\" data-if=\"settings.heading\">{{ settings.heading }}</h2>\n  <ul class=\"stats__list\">\n    <li class=\"stats__item\" data-each=\"settings.stats\">\n      <span class=\"stats__value\"><span data-count-up=\"{{ item.value }}\">0</span>{{ item.suffix }}</span>\n      <span class=\"stats__label\">{{ item.label }}</span>\n    </li>\n  </ul>\n</section>",
          "css": ":scope { padding: 4rem 1.5rem; background: var(--setting-background); color: var(--color-neutral-900); }\n.stats__heading { margin: 0 0 2rem; text-align: center; }\n.stats__list { display: grid; grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr)); gap: 2rem; margin: 0; padding: 0; list-style: none; }\n.stats__item { text-align: center; opacity: 0; transform: translateY(1rem); }\n:scope[data-visible] .stats__item { animation: custom-stats-band-rise 600ms ease-out calc(var(--i) * 120ms) both; }\n.stats__value { display: block; font-size: 2.5rem; font-weight: 700; color: var(--color-primary-700); }\n@keyframes custom-stats-band-rise { to { opacity: 1; transform: none; } }\n@media (prefers-reduced-motion: reduce) { .stats__item { opacity: 1; transform: none; } :scope[data-visible] .stats__item { animation: none; } }"
        }
        """;

    private const string KitKey = "kit";

    public static object Describe(string storeId, JsonObject catalog, CustomSectionRules rules)
    {
        var kit = catalog?[KitKey] as JsonObject;
        var example = (JsonObject)JsonNode.Parse(ExampleFile)!;

        return new
        {
            StoreId = storeId,
            Available = catalog != null,
            Reason = catalog == null
                ? "The storefront has not sent its schema catalog yet: its kit tags, behaviors and palette tokens are not known, and a type " +
                  "is not saved until it has. Open the store once in the designer; its preview sends the catalog."
                : null,
            Workflow = new[]
            {
                $"Read site-builder://stores/{storeId}/schemas first: a built-in section may do it already, or a custom type (custom: true) may be close; " +
                    $"read one at site-builder://stores/{storeId}/custom-sections/{{type}}.",
                "Write the type: every text a customer reads in a translatable setting, colors from the palette, motion from the kit's behaviors.",
                "Call create_section_type with dryRun, fix every error and the warnings you can, and call it again until it answers no errors; then create it. " +
                    "Change a type with change_section_type: every page that places it changes with it.",
                "Place it with edit_page_draft (add_section with the type and its settings); each page has its own settings.",
                "Publish the theme first (publish_theme), then the page (publish_page): the storefront renders only a published type.",
            },
            File = new
            {
                Path = $"{ThemeStorage.CustomSectionsFolder}/{{type}}.json in the theme; create_section_type and change_section_type write its draft.",
                Type = "'custom-' and lowercase words joined by '-', at most 64 characters, such as custom-stats-band. Keep the type of one a page places.",
                Name = "A text, or a text per language: the name in the designer's section picker.",
                SchemaKeys = rules.SchemaKeys,
                FieldTypes = rules.FieldTypes,
                FieldKeys = rules.FieldKeys,
                Fields = new[]
                {
                    "A setting is { id, type, label, ... } as a section schema declares it; header and paragraph are labels and hold no value.",
                    "An id is letters, digits and '_', starting with a letter.",
                    "string holds a text, with \"format\": \"url\" a link; text holds rich text (HTML); string and text take \"translatable\": true.",
                    "select has options, each { value, label }; list and object have element, the fields of a row or of the object, one level deep.",
                    "images holds one image of the asset library; color-token holds a palette token below, such as color_primary_500.",
                    "A setting holds one value: no multiple.",
                },
            },
            Template = new
            {
                Root = "One root element, the section's root: the storefront adds data-sb-type, the preview's attributes and the setting variables in its style.",
                Elements = rules.Elements,
                ElementsNote = "And the kit tags below. Anything else is refused, such as svg, script, style, iframe and form controls.",
                Attributes = rules.Attributes,
                AttributesNote = "And aria-*, the binding attributes and the behaviors below. No id (two sections on a page would share it), no style " +
                    "(the CSS styles it), no on* (script).",
                Links = $"href is a path starting with '/', a URL starting with {JoinOr(rules.LinkSchemes)}, or one binding of a string setting with \"format\": \"url\".",
                Images = "src and srcset are a URL of the store's asset library (search_assets), or one binding of an images setting.",
                Text = $"Text written into the template, or into {JoinOr(rules.TextAttributes)}, is the same in every language and saves with a " +
                    "warning: bind it to a translatable setting.",
            },
            Bindings = new
            {
                Attributes = rules.BindingAttributes,
                Rules = new[]
                {
                    "{{ settings.<id> }} in a text or an attribute value inserts the value as text, never as HTML; {{ settings.<id>.<field> }} reads a field of an object setting.",
                    "data-each=\"settings.<id>\" repeats the element once per row of a list setting; inside it {{ item.<field> }} reads the row. Each copy gets --i (from 0) and --n. Not on the root, not nested.",
                    "data-if=\"settings.<id>\" or data-if=\"item.<field>\", with a leading ! for the opposite, renders the element only when the value is set: not false, \"\", null or an empty list.",
                    "data-html=\"settings.<id>\" or data-html=\"item.<field>\" of a text setting fills the empty element with its HTML, which the storefront sanitizes.",
                    "A path names a declared setting: there are no expressions, filters or code.",
                    "A translatable value is bound in the page's language.",
                },
            },
            SettingVariables = new
            {
                Rule = "The root's style sets --setting-<id>, with '_' written as '-', for these settings; the CSS reads them, such as background: var(--setting-background).",
                Values = new Dictionary<string, string>
                {
                    ["color-token"] = "the token's variable, such as var(--color-primary-500) for color_primary_500",
                    ["images"] = "url(\"<url>\")",
                    ["number, slider"] = "the number",
                    ["checkbox"] = "1 or 0",
                },
            },
            Css = new
            {
                Scope = "The storefront wraps the CSS in @layer sections { @scope ([data-sb-type=\"<type>\"]) { ... } }: it reaches only the type's sections, and :scope is the section's root.",
                AtRules = rules.AtRules,
                Keyframes = "A @keyframes name starts with the type, such as custom-stats-band-rise: names are not scoped.",
                Refused = new[]
                {
                    "any other at-rule, such as @import, @font-face, @layer or @property",
                    "a selector naming :root, html or body",
                    "position: fixed",
                    "url() of anything but the store's asset library",
                    "expression(), behavior and -moz-binding",
                },
                Colors = "From the palette: var(--color-...) of the tokens below, a color-token setting's --setting-<id>, transparent or currentColor. A literal " +
                    "color saves with a warning: neither the palette nor the dark theme changes it.",
                Classes = "The template's own classes: the storefront's utility classes are not available.",
                Motion = "With prefers-reduced-motion the behaviors show their end state at once; turn your own animations off in @media (prefers-reduced-motion: reduce).",
            },
            Palette = new
            {
                Tokens = GetPaletteTokens(catalog),
                Note = "A color-token setting holds the token; the CSS reads a token's color as its variable.",
            },
            Kit = new
            {
                Tags = kit?["tags"]?.DeepClone() ?? new JsonObject(),
                Behaviors = kit?["behaviors"]?.DeepClone() ?? new JsonObject(),
                Usage = new[]
                {
                    "A kit tag renders a storefront component, such as <vc-button label=\"{{ settings.cta }}\" to=\"{{ settings.link }}\"></vc-button>. " +
                        "It has no content, and takes its attributes, data-if and data-each.",
                    "A behavior is switched on with data-<name>, whose value, when it takes one, may be a binding, and set with data-<name>-<option> on the " +
                        "element or inside it. It writes what its writes names, such as data-visible, --scroll-progress or the text: style that in the CSS.",
                    "An attribute's or an option's value is a literal its type takes, or one binding.",
                },
                Note = kit == null
                    ? "The storefront's catalog names no kit: a type that uses a kit tag or a behavior is refused."
                    : "A kit tag or a behavior the catalog does not name is refused.",
            },
            Limits = new
            {
                TemplateBytes = rules.MaxPartSize,
                CssBytes = rules.MaxPartSize,
                ThemeBytes = rules.MaxTotalSize,
                Note = "theme_bytes is the JSON of every type of the theme together: the snapshot carries them to every page.",
            },
            Example = new
            {
                Type = ExampleType,
                Note = "The arguments of create_section_type for a band of numbers that count up as it scrolls into view; it uses the behaviors inview and count-up.",
                Name = example["name"]?.DeepClone(),
                Schema = example["schema"]?.DeepClone(),
                Template = example["template"]?.DeepClone(),
                Css = example["css"]?.DeepClone(),
            },
        };
    }

    private static string JoinOr(IList<string> items) =>
        items.Count > 1 ? $"{string.Join(", ", items.Take(items.Count - 1))} or {items[^1]}" : string.Join(string.Empty, items);

    // A token's variable is the token with '_' written as '-': color_primary_500 is --color-primary-500.
    private static List<PaletteTokenRow> GetPaletteTokens(JsonObject catalog)
    {
        var tokens = ThemeFiles.GetPaletteTokens(catalog);

        return tokens.Light.Concat(tokens.Dark)
            .Distinct(StringComparer.Ordinal)
            .Select(x => new PaletteTokenRow(x, $"--{x.Replace('_', '-')}"))
            .ToList();
    }
}

public sealed record PaletteTokenRow(string Token, string Variable);
