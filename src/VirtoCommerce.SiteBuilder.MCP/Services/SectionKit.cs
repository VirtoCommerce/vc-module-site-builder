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
          "template": "<section class=\"custom-stats-band\" data-inview data-inview-once>\n  <h2 class=\"custom-stats-band__heading\" data-if=\"settings.heading\">{{ settings.heading }}</h2>\n  <ul class=\"custom-stats-band__list\">\n    <li class=\"custom-stats-band__item\" data-each=\"settings.stats\">\n      <span class=\"custom-stats-band__value\"><span data-count-up=\"{{ item.value }}\">0</span>{{ item.suffix }}</span>\n      <span class=\"custom-stats-band__label\">{{ item.label }}</span>\n    </li>\n  </ul>\n</section>",
          "css": ":scope { padding-block: 2.5rem; padding-inline: 1rem; background: var(--setting-background); color: var(--color-neutral-900); font-family: var(--vc-base-font-family, inherit); }\n.custom-stats-band__heading { margin-block: 0 1.5rem; margin-inline: 0; font-family: var(--vc-heading-font-family, inherit); font-size: 1.5rem; line-height: 1.25; text-align: center; }\n.custom-stats-band__list { display: grid; grid-template-columns: minmax(0, 1fr); gap: 1rem; margin: 0; padding: 0; list-style: none; }\n.custom-stats-band__item { padding: 1rem; border-radius: var(--vc-radius, 0.5rem); background: var(--color-additional-50); text-align: center; overflow-wrap: anywhere; opacity: 0; transform: translateY(1rem); }\n:scope[data-visible] .custom-stats-band__item { animation: custom-stats-band-rise 600ms ease-out calc(var(--i) * 120ms) both; }\n.custom-stats-band__value { display: block; font-family: var(--vc-heading-font-family, inherit); font-size: 2.25rem; font-weight: 700; line-height: 1.1; color: var(--color-primary-700); }\n@container sb-section (min-width: 40rem) { :scope { padding-block: 4rem; } .custom-stats-band__list { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1.5rem; } }\n@container sb-section (min-width: 64rem) { .custom-stats-band__heading { font-size: 1.875rem; } .custom-stats-band__list { grid-template-columns: repeat(4, minmax(0, 1fr)); } }\n@keyframes custom-stats-band-rise { to { opacity: 1; transform: none; } }\n@media (prefers-reduced-motion: reduce) { .custom-stats-band__item { opacity: 1; transform: none; } :scope[data-visible] .custom-stats-band__item { animation: none; } }"
        }
        """;

    private const string KitKey = "kit";
    private const string DesignKey = "design";
    private const string GuideKey = "guide";

    // what the server checks, and the few rules any section keeps; the storefront's guide says the rest
    private static readonly string[] DesignRules =
    [
        "Follow storefront_guide first; these rules add what a save checks or every section keeps.",
        "Mobile first: base styles for the narrow width, widened with @container sb-section (min-width: ...), the container the storefront " +
            "wraps each section in (storefront_design names it containerName). @media is only for the person's preferences, such as " +
            "prefers-reduced-motion, and for print.",
        "Read the exact token names in storefront_design: fonts, font sizes, line heights, font weights, letter spacing, radii and shadows " +
            "are var(--vc-..., fallback) with the fallback it lists. There are no spacing variables: spacing is rem on a 0.25rem step. Colors " +
            "are palette variables, var(--color-<family>-<shade>), which the dark theme changes with them.",
        "No h1, since the page has it: headings start at h2 and go down one level at a time.",
        "An image's alt is bound to a translatable setting, or is \"\" for an image that only decorates.",
        "A link has text a person reads, bound to a translatable setting.",
        "Never remove the focus outline, with outline: none or outline: 0.",
        "Anything that moves stops in @media (prefers-reduced-motion: reduce).",
        "German and French run about 30% longer than English: no fixed width or height on an element that holds text.",
        "Right-to-left languages: logical properties only, such as margin-inline-start, padding-block and inset-inline-end, never left or " +
            "right; mirror what is left, such as an arrow, with :dir(rtl). A save warns of a physical direction.",
    ];

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
            StorefrontGuide = GetString(kit?[GuideKey]) ?? "Not available: the storefront's catalog has no guide; follow design_rules.",
            StorefrontDesign = kit?[DesignKey]?.DeepClone() ?? JsonValue.Create("Not available: the storefront's catalog names no design, so its " +
                "breakpoints and token names are not known. Use only --vc-radius, --vc-base-font-family and --vc-heading-font-family, each with a " +
                "fallback, and the palette variables."),
            DesignRules = DesignRules,
            Workflow = new[]
            {
                $"Read site-builder://stores/{storeId}/schemas first: a built-in section may do it already, or a custom type (custom: true) may be close; " +
                    $"read one at site-builder://stores/{storeId}/custom-sections/{{type}}.",
                "Write the type: every text a customer reads in a translatable setting, colors from the palette, motion from the kit's behaviors.",
                "Call create_section_type with dryRun, fix every error and the warnings you can, and call it again until it answers no errors; then create it. " +
                    "Change a type with change_section_type: every page that places it changes with it.",
                "Place it with edit_page_draft (add_section with the type and its settings), each page with its own settings, or in a template with " +
                    "edit_template; a header or a footer cannot place a custom type.",
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
                Root = "One root element, the section's root: it keeps data-sb-type and the setting variables in its style. The storefront renders each " +
                    "section in a wrapper of its own, with contain: paint and isolation: isolate, that holds the anchor id and the preview's attributes.",
                Markup = $"HTML as written: markup a parser has to mend, such as an element left open or a /> on a kit tag, is refused, and so are " +
                    $"elements nested more than {rules.MaxTemplateDepth} deep, the root counted.",
                Elements = rules.Elements,
                ElementsNote = "And the kit tags below. Anything else is refused, such as svg, script, style, iframe and form controls.",
                Attributes = rules.Attributes,
                AttributesNote = "And aria-*, the binding attributes and the behaviors below. No id (two sections on a page would share it), no style " +
                    "(the CSS styles it), no on* (script).",
                Classes = $"class names the type's own classes only: the type itself, or a name starting with the type and {JoinOr(rules.ClassSeparators.Select(x => $"'{x}'").ToList())}, " +
                    "such as custom-stats-band__value or custom-stats-band-dark. A binding in class names a select setting whose options are such classes.",
                Links = $"href is a path starting with '/', a URL starting with {JoinOr(rules.LinkSchemes)}, or one binding of a string setting with \"format\": \"url\".",
                LinkTargets = $"target is {JoinOr(rules.LinkTargets)}, and rel does not hold {JoinOr(rules.RefusedRels)}; both are written as they are, with no binding.",
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
                    ["color-token"] = "the token's variable, such as var(--color-primary-500) for color_primary_500; color_ is put before a token without it",
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
                Reading = "The CSS is read as a browser reads it, from its tokens: a comment inside a string is text, and escapes are read.",
                Refused = new[]
                {
                    "any other at-rule, such as @import, @font-face, @layer or @property",
                    "a selector naming :root, html or body",
                    $"position: fixed, and {JoinOr(Functions(rules.LateValueFunctions))} in position",
                    $"a URL other than one of the store's asset library in {JoinOr(Functions(rules.ImageFunctions))}, custom properties included, and " +
                        $"{JoinOr(Functions(rules.LateValueFunctions))} inside them",
                    $"expression(), and the properties {JoinOr(rules.ScriptProperties)}, which run script",
                    "a string that a line break cuts off, and a url( whose unquoted URL holds a quote, a parenthesis or a space: quote the URL",
                    $"a block left open or closed without its pair, blocks nested deeper than {rules.MaxCssDepth}, and at-rules nested deeper than {rules.MaxAtRuleDepth}",
                },
                Colors = "From the palette: var(--color-...) of the tokens below, a color-token setting's --setting-<id>, transparent or currentColor. A literal " +
                    "color saves with a warning: neither the palette nor the dark theme changes it.",
                Classes = "The type's own classes, as the template's class names them: the storefront's utility classes are not available.",
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
                        "It has no content and an end tag of its own, and takes its attributes, data-if and data-each.",
                    $"Whatever the catalog declares, a kit tag takes no {JoinOr(rules.KitDeniedAttributes)}, and {JoinOr(rules.KitLinkAttributes)} follow the " +
                        "link rule of href.",
                    "A behavior is switched on with data-<name> and set with data-<name>-<option> on the element or inside it. Its own value is checked by " +
                        "the type the kit declares under value, such as a number for count-up or a date for countdown; one that declares none takes any text. " +
                        "It writes what its writes names, such as data-visible, --scroll-progress or the text: style that in the CSS.",
                    $"An attribute's, an option's or a value's type is {JoinOr(rules.KitFieldTypes)}: a select takes one of its options, a number one from " +
                        "min to max, a boolean true, false or no value, a date an ISO 8601 date or date and time, such as 2026-12-31, and a link follows " +
                        "the link rule. Each is a literal of its type, or one binding.",
                    "The storefront's own kit tags and behaviors keep their names: a module of the storefront cannot register one of the same name.",
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
                TemplateDepth = rules.MaxTemplateDepth,
                CssDepth = rules.MaxCssDepth,
                AtRuleDepth = rules.MaxAtRuleDepth,
                Note = "theme_bytes is the JSON of every type of the theme together: the snapshot carries them to every page.",
            },
            Example = new
            {
                Type = ExampleType,
                Note = "The arguments of create_section_type for a band of numbers that count up as it scrolls into view, following the design rules: " +
                    "one column on a narrow section and more as it widens, spacing in rem, the token variables that certainly exist with their " +
                    "fallbacks, and the behaviors inview and count-up.",
                Name = example["name"]?.DeepClone(),
                Schema = example["schema"]?.DeepClone(),
                Template = example["template"]?.DeepClone(),
                Css = example["css"]?.DeepClone(),
            },
        };
    }

    private static string GetString(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static List<string> Functions(IEnumerable<string> names) => names.Select(x => $"{x}()").ToList();

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
