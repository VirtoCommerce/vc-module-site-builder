# Custom sections

A custom section is a section type defined in the store's theme rather than built into the storefront: an HTML
template, its CSS and a settings schema. An agent writes one with the MCP tools; the designer places it like any other
section. The storefront renders it from the published [snapshot](theme-snapshot.md#snapshot), or from the
[draft](preview-protocol.md#draft) in the preview, with no build and no release. One type is placed on any number of
pages, each with its own settings.

A custom section runs no code of its own. Its template is data: elements, attributes and bindings from the lists
below. Anything that needs logic comes from the storefront: a [kit tag](#kit-tags) or a [behavior](#behaviors).

## File

`custom-sections/{type}.json` in the theme folder, with its draft `{file}-draft` next to it as every theme file has.
`type` matches `^custom-[a-z0-9]+(?:-[a-z0-9]+)*$` and is at most 64 characters; the file name is the type. Writing it
takes `builder:theme`. A type is never renamed once a page or a template places it. A theme's types are at most 256 KB
of JSON together, since the snapshot carries every type to every page.

```ts
interface CustomSectionType {
  format: 1;
  /** the name in the designer's section picker; translatable */
  name: string | Record<string, string>;
  schema: {
    /** the settings, as a section schema declares them */
    settings: Field[];
    /** the templates it may be placed on; any when left out */
    targetTemplates?: string[];
    limit?: number;
    icon?: string;
  };
  /** the HTML template, at most 64 KB */
  template: string;
  /** the CSS, at most 64 KB */
  css: string;
}
```

`Field` is a field of a section schema ([Schemas](preview-protocol.md#schemas)). The types a custom section takes are
`header`, `paragraph`, `string` (a link is `"format": "url"`), `text` (rich text), `number`, `slider`, `checkbox`,
`select` with `options`, `color-token`, `images` (single), `calendar` and `list` with `element` of these, one level
deep. `string` and `text` may be `"translatable": true`.

## Template

An HTML fragment with exactly one root element, the section's root. The storefront adds to the root
`data-sb-type="{type}"`, the preview attributes of a section and a `style` holding the
[setting variables](#setting-variables). It is HTML as written: markup an HTML parser has to mend, such as an element
left open or a `/>` on a kit tag, is refused, and so are elements nested more than 32 deep, the root counted.

- **Elements**: the elements the builder's HTML sanitizer keeps, without form controls (`form`, `input`, `select`,
  `textarea`, `button`, `option`, `optgroup`, `datalist`, `fieldset`, `legend`, `label`, `output`), plus the
  [kit tags](#kit-tags). `svg`, `script`, `style`, `template`, `iframe` and the other elements the sanitizer drops with
  their content are refused.
- **Attributes**: `class`, `role`, `aria-*`, `alt`, `title`, `lang`, `dir`, `hidden`, `href`, `target`, `rel`, `src`,
  `srcset`, `sizes`, `loading`, `decoding`, `width`, `height`, `datetime`, `colspan`, `rowspan`, `scope`,
  `data-each`, `data-if`, `data-html`, and the [behavior](#behaviors) attributes. No `id` (two instances on a page would
  share it), no `style`, no `on*`.
- **Classes**: `class` names the type's own classes only: the type itself, or a name starting with `{type}-` or
  `{type}__` (`custom-hero__title`, `custom-hero--dark`). A binding in it names a `select` setting whose options are such
  classes.
- **Link targets**: `target` is `_self` or `_blank`, and `rel` does not hold `opener`; both are written as they are, with
  no binding.
- **Links and images**: `href` is a path starting with `/`, an `https:`, `mailto:` or `tel:` URL, or a binding of a
  `"format": "url"` field. `src` and `srcset` are a URL of the store's asset library or a binding of an `images`
  field. A bound value that breaks the rule renders without the attribute.

### Bindings

Bindings name settings by path; there are no expressions.

- `{{ settings.<id> }}`, and `{{ settings.<id>.<field> }}` into an `object`, in a text or an attribute value. The value
  is inserted as text, never as HTML. A path must name a declared setting.
- `data-each="settings.<id>"` on an element repeats it once per row of a `list` setting; inside it `item.<field>`
  names the row's fields. The storefront sets `--i` (from 0) and `--n` on each copy. Not on the root, not nested.
- `data-if="settings.<id>"`, `data-if="item.<field>"`, or with a leading `!`: the element renders only when the value
  is set (not `false`, `""`, `null` or an empty list), or, with `!`, only when it is not.
- `data-html="settings.<id>"` (or `item.<field>`) names a `text` field; the element must be empty and gets the value as
  HTML, sanitized by the storefront.

A translatable value is bound in the page's language. Text written into the template itself is the same in every
language: the save answers it with a warning.

### Kit tags

A kit tag renders a storefront component. The storefront names its kit tags in the catalog's [`kit`](#catalog), and a
module or a plugin of the storefront may add its own; the storefront registers these:

| Tag | Attributes | Renders |
|---|---|---|
| `vc-button` | `label`, `to`, `color`, `variant`, `size`, `prepend-icon`, `append-icon`, `full-width` | `VcButton`; `to` follows the link rule |
| `vc-icon` | `name`, `size`, `color` | `VcIcon` |
| `vc-badge` | `label`, `color`, `variant`, `size` | `VcBadge` |
| `vc-products` | `query`, `filter`, `take` (1–24), `view` (`grid` or `carousel`), `columns` (1–6) | the product list or carousel of the built-in sections |

A kit tag's name starts with `vc-`, and an element named `vc-…` that the catalog does not name is refused. Its
attributes are those the catalog declares, and `data-if` and `data-each`; they take literals or bindings, and a literal
the attribute's type does not take is refused on save. Whatever the catalog declares, a kit tag takes no `on*`, `style`,
`id`, `is`, `class`, `srcdoc` or `form*` attribute, and `to`, `href` and `src` follow the link rule. A kit tag has no
content.

### Behaviors

A behavior is a storefront directive an attribute switches on. It reads the element and writes CSS variables, attributes
or the element's text; the visuals are the section's CSS. With `prefers-reduced-motion: reduce` a behavior shows its end
state at once. The catalog's [`kit`](#catalog) names the behaviors, as it names the kit tags: `data-{name}` is a behavior
when the catalog has `{name}`, and `data-{name}-{option}` is one of its options, on the element with the behavior or
inside it, its value checked by the option's type. The storefront registers these:

| Attribute | Options | Effect |
|---|---|---|
| `data-inview` | `data-inview-threshold` (0–1, default 0.2), `data-inview-once` | sets `data-visible` while that share of the element is on screen; with `-once` it stays |
| `data-scroll-progress` | | `--scroll-progress` from 0 (the element enters the viewport) to 1 (it leaves) |
| `data-pointer` | | `--pointer-x`, `--pointer-y` (0–1 inside the element) and `--pointer-active` (0 or 1); off on touch screens |
| `data-count-up="{number}"` | `data-count-up-duration` (ms, up to 5000), `data-count-up-decimals` (0–4) | the text counts up to the number, formatted in the page's language, when the element first shows |
| `data-countdown="{date}"` | | children with `data-countdown-part="days"`, `hours`, `minutes` or `seconds` show the time left until the ISO 8601 date; `data-ended` is set when it passes |
| `data-copy="{text}"` | | a click copies the text and sets `data-copied` for 2 seconds |
| `data-marquee` | | the children are repeated once for a seamless loop; `--marquee-width` is the width of one run |

A behavior may declare the type of its own attribute's value under `value`, such as a number for `data-count-up` and a
date for `data-countdown`, and the value is checked as an option's is; a behavior that declares none takes any text.
Either may be a binding.

## Setting variables

The root's `style` sets a variable per setting the CSS can use, named `--setting-{id}` with `_` written as `-`:

| Field | Value |
|---|---|
| `color-token` | `var(--color-{token})`, the token with `_` written as `-` |
| `images` | `url("{url}")` |
| `number`, `slider` | the number |
| `checkbox` | `1` or `0` |

## CSS

The storefront applies a type's CSS once per page, wrapped so it reaches only the type's sections:
`@layer sections { @scope ([data-sb-type="{type}"]) { … } }`. `:scope` is the section's root.

- **At-rules**: `@media`, `@supports`, `@container` and `@keyframes`. A keyframes name starts with the type
  (`@keyframes custom-hero-fade`), since names are not scoped.
- **Refused**: other at-rules (`@import`, `@font-face`, `@layer`, `@scope`, `@property`, `@page`, `@namespace`); a
  selector naming `:root`, `html` or `body`; `position: fixed`, and `var()`, `attr()` or `env()` in `position`; a URL
  other than an asset library URL, in `url()` or as a string of `image-set()` and the other functions that load an
  image, custom properties included, and `var()`, `attr()` or `env()` inside such a function; `expression()`,
  `behavior` and `-moz-binding`.
- **Read as a browser reads it**, from its tokens: a comment inside a string is text, and escapes are read. A string
  that a line break cuts off, and a `url(` whose unquoted URL holds a quote, a parenthesis or a space, are refused, and
  so is a block left open or closed without its pair, since the storefront's wrapping would close with it. Blocks nest
  at most 32 deep, and at-rules at most 4.
- **Colors** come from the palette: `var(--color-…)`, a `color-token` setting's variable, or `transparent` and
  `currentColor`. A literal color is saved with a warning.
- **Classes** are the type's own, as the template's `class` names them. The storefront's utility classes are not
  available: they would style the section from outside its CSS, such as fixed over the page.

## Validation

A save and a publication check every custom section type of the theme and refuse a type that breaks a rule above with
`custom-sections/{type}.json: …`, naming the rule; nothing is removed silently. Warnings do not refuse. A template or a
page may place a custom type the theme holds: a save checks the theme's drafts, a publication the theme it publishes. A
header or footer group may not, since the storefront renders only the group's own sections there: a save and a
publication refuse it. Kit tags and behaviors are checked against the `kit` of the catalog the save names,
or the publication publishes with; with no catalog, or a catalog without `kit`, a type that uses one is refused.

The storefront checks again when it renders, since the preview's draft comes from the designer: an element, attribute,
binding or rule the lists above do not allow is left out.

## Catalog

The storefront adds each custom type to the catalog's `sections` under its type, with its `name`, `settings`,
`targetTemplates`, `limit` and `icon`, and `"custom": true`. The `name` is as the type stores it: a text, or a text per
language. It sends `loaded` again when the draft changes the set of
custom types.

The catalog's `kit` names the kit tags and the behaviors the storefront offers, its own and those its modules add:

```ts
interface Kit {
  tags: Record<string, { attributes: Record<string, KitField>; description?: string }>;
  /**
   * `value` is the type of the behavior attribute's own value, any text when left out; `writes` names what the behavior
   * sets for the section's CSS, such as `data-visible` or `--scroll-progress`
   */
  behaviors: Record<string, { value?: KitField; options: Record<string, KitField>; description?: string; writes?: string[] }>;
}

interface KitField {
  /**
   * a select takes one of its options, a number one from min to max, a boolean true, false or no value, a date an ISO
   * 8601 date or date and time, and a link follows the link rule
   */
  type: 'string' | 'number' | 'boolean' | 'select' | 'date' | 'link';
  options?: (string | { value: string; label?: string })[];
  min?: number;
  max?: number;
  description?: string;
}
```

A tag whose name does not start with `vc-` is left out. A storefront whose catalog has no `kit` offers no kit tags and no
behaviors. The catalog's upload refuses a `kit` of another shape: a tag not named `vc-` and lowercase words, an attribute
no kit tag takes, a behavior named `each`, `if` or `html`, or a field of another type or with other keys. A kit tag or
a behavior that a catalog stored earlier declares with a field of another type is refused where a template uses it.
