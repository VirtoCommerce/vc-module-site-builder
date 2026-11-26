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
takes `builder:theme`. A type is never renamed once a page or a template places it. Discarding the draft of a type that
was never published removes the type for good: the draft is its only copy. The dry run of `discard_draft` says so by the
type's name and counts the pages that place it. The prefix `custom-` is the theme's: the catalog's upload refuses a
section whose key starts with it unless it is marked `"custom": true`. A theme's types are at most 256 KB of JSON
together, and at most 256 types, since the snapshot carries every type to every page; the setting
`SiteBuilder:ThemeSnapshot:CustomSectionsMaxSize` (bytes) changes the size limit. A save checks the limits early, but
two saves at once may pass them together: the publication enforces them, and `get_publish_readiness` names them. A save
is refused on a limit only when it grows the types past it, and then on the limits alone, before the types it writes
are checked; types already over a limit, after two such saves or a lower limit, can still shrink. The publication holds
the limits as they are.

```ts
interface CustomSectionType {
  format: 1;
  /**
   * the name in the designer's section picker: a text, or a text per language keyed by culture names, at least one with a
   * region (`en-US`), as the snapshot translates it
   */
  name: string | Record<string, string>;
  schema: {
    /** the settings, as a section schema declares them */
    settings: Field[];
    /**
     * the templates that offer it, by their keys in the catalog's `templates`, as the `schemas` resource lists them; any
     * when left out. A save warns of a key the catalog does not name.
     */
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
`select` with `options`, `color-token`, `images` (single), `calendar`, and `list` and `object` with `element` of these,
one level deep: the fields of a list's rows or of an object. `string` and `text` may be `"translatable": true`.

A setting's `id` matches `^[A-Za-z][A-Za-z0-9_]*$` and is its own among its siblings; `header` and `paragraph` have
none. No setting, a row's field included, is named `id`, `type`, `hidden`, `blocks` or `__group`: the designer writes a
setting's value next to these keys of the section itself. A setting takes `id`, `type`, `label`, `default`,
`translatable`, `tab`, `info`, `placeholder`, `required`, `multiline`, `format`, `options`, `element`, `min`, `max` and
`step`. `visibility` and `request` are refused, since the designer runs the one and sends the other, and so are
`"multiple": true` and any other key. A setting's `label`, `info` and `placeholder` are texts, as the designer shows
them, and so is the `label` of a `select`'s option. A `default` is one the setting takes, or empty: a `string`'s, a
`text`'s and a `calendar`'s are texts, and a translatable setting's may be a text per language; a link's follows the
link rule, an `images` one is a URL of the store's asset library, a `calendar`'s is an ISO 8601 date or a date and a
time with its offset, a `select`'s is one of its options, a `number`'s or a `slider`'s is a number within its `min` and
`max`, a `checkbox`'s is `true` or `false`, and a `color-token`'s is one of the catalog's palette tokens as the catalog
lists them, with `color_`, when it lists any; a `header` and a `paragraph` hold no value and take none. A `list`'s
default is a list of rows and an `object`'s one row, each field of which holds a default that field takes. A default
applies wherever a placed section has no value of its own, a setting added to the type after the section was placed
included.

## Template

An HTML fragment with exactly one root element, the section's root. The storefront renders each section inside a
wrapper element of its own, outside the scope root, with `contain: paint` and `isolation: isolate`, so that nothing a
section draws leaves its box; the wrapper carries the section's anchor id and the preview attributes of a section. The
root keeps `data-sb-type="{type}"` and a `style` holding the [setting variables](#setting-variables), so the root is an
element of HTML, not a kit tag. The type's CSS is scoped to that attribute, so an element of a page's rich text that
writes it gets the type's rules too, outside a wrapper. It is HTML as written, read as the storefront's sanitizer reads it, with no doctype
(quirks mode, where a `table` may stay inside a `p`): markup the parser has to mend, such as an element left open or a
`/>` on a kit tag, is refused at the first place it breaks, where the parse stops, and so are elements nested more than
32 deep, the root counted. A template of more than 1,500 tags, counting each `<` followed by a letter as written, in
comments and attribute values too, or with a tag of more than 64 attributes, counted as written, is refused before it
is parsed.

- **Elements**: the elements the builder's HTML sanitizer keeps, without form controls (`form`, `input`, `select`,
  `textarea`, `button`, `option`, `optgroup`, `datalist`, `fieldset`, `legend`, `label`, `output`), plus the
  [kit tags](#kit-tags). `svg`, `script`, `style`, `template`, `iframe` and the other elements the sanitizer drops with
  their content are refused.
- **Attributes**: `class`, `role`, `aria-*` but those that name an element by id (`aria-activedescendant`,
  `aria-controls`, `aria-describedby`, `aria-details`, `aria-errormessage`, `aria-flowto`, `aria-labelledby`,
  `aria-owns`: a section has no id, so they would name the page around it), `alt`, `title`, `lang`, `dir`, `hidden`, `href`, `target`, `rel`, `src`,
  `srcset`, `sizes`, `loading`, `decoding`, `width`, `height`, `datetime`, `colspan`, `rowspan`, `scope`,
  `data-each`, `data-if`, `data-html`, and the [behavior](#behaviors) attributes. `video` also takes `autoplay`,
  `muted`, `loop`, `playsinline`, `controls`, `preload` and `poster`; `source` takes `type` and `media`; `details` takes
  `open`; `blockquote` and `q` take `cite`, which follows the link rule. No `id` (two instances on a page would share
  it), no `style`, no `on*`.
- **Classes**: `class` names the type's own classes only: the type itself, or a name starting with `{type}-` or
  `{type}__` (`custom-hero__title`, `custom-hero--dark`). A binding in it names a `select` setting whose options are such
  classes.
- **Link targets**: `target` is `_self` or `_blank`, case counting, and `rel` does not hold `opener`; both are written
  as they are, with no binding.
- **Links and images**: `href` is a path starting with a single `/` (not `//`), an anchor on the page (`#` and a letter,
  then letters, digits, `_` and `-`), an `https:`, `mailto:` or `tel:` URL, or a binding of a `"format": "url"` field; a
  link holds no backslash, tab or line break, which a browser could read as leaving the site. `src`, `srcset` and a
  `video`'s `poster` are a URL of the store's asset library or a binding of an `images` field. A URL of the library is
  read as written: its origin is the library's (case aside), no user info, and its path, once its `..` is resolved,
  starts with the library's path (case counts); one with an escaped dot, slash or backslash (`%2e`, `%2f`, `%5c`) or a
  backslash is refused. A bound value that breaks the rule renders without the attribute, and so does a link or an image
  in a `text` setting's HTML: a placed section's values are not checked on save, so an `http:` link, such as a rich-text
  editor writes for a typed `www.example.com`, or an image from outside the library saves and renders without its `href`
  or `src`.

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
module or a plugin of the storefront may add its own. The storefront registers these, and their names are reserved: a
module cannot register a kit tag of the same name.

| Tag | Attributes | Renders |
|---|---|---|
| `vc-button` | `label`, `to`, `color`, `variant`, `size`, `prepend-icon`, `append-icon`, `full-width` | `VcButton`; `to` follows the link rule, and the icons are of `kit.icons` |
| `vc-icon` | `name`, `size`, `color` | `VcIcon`; `name` is one of the catalog's `kit.icons` |
| `vc-badge` | `label`, `color`, `variant`, `size` | `VcBadge` |
| `vc-products` | `query`, `filter`, `take` (1–24), `view` (`grid` or `carousel`), `columns` (1–6) | the product list or carousel of the built-in sections |
| `vc-image` | `src`, `alt`, `width`, `height`, `lazy` | `VcImage`; `src` follows the image rule, and `alt` is `""` unless written |
| `vc-link` | `label`, `to` | `VcLink`; without `to`, its text alone |
| `vc-chip` | `label`, `to`, `icon`, `color`, `variant`, `size`, `rounded` | `VcChip`; a link with `to` |
| `vc-alert` | `text`, `title`, `color`, `variant`, `size`, `icon`, `with-icon`, `shadow` | `VcAlert`, with no close button |
| `vc-typography` | `text`, `tag` (`p`, `span`, `div`, `h2`–`h6`), `variant`, `color`, `truncate` | `VcTypography` |
| `vc-rating` | `value` (0–5), `mode`, `size`, `with-text`, `review-count`, `label` | `VcRating`, read only |
| `vc-shape` | `icon`, `icon-color`, `bg-color`, `size` (pixels), `mask` | `VcShape`; `icon` is of `kit.icons`, and `mask` of `kit.masks` |
| `vc-property` | `label`, `value` | `VcProperty` |

A kit tag's name starts with `vc-`, and an element named `vc-…` that the catalog does not name is refused. Its
attributes are those the catalog declares, and `data-if` and `data-each`; they take literals or bindings, and a literal
the attribute's type does not take is refused on save. A boolean attribute is on unless it says `false`; bound to a
setting that is not set, it is left out, so it is off. The native boolean attributes, `hidden`, `video`'s `autoplay`,
`muted`, `loop`, `playsinline` and `controls`, and `details`'s `open`, follow the same rule: one that says `false`, such
as `open="false"`, which HTML alone would read as on, or is bound to a setting that is not set, is off. Whatever the
catalog declares, a kit tag takes no `on*`, `style`, `id`, `is`, `class`, `srcdoc`, `form*`, `popover*`, `ping`,
`srcset`, `poster`, `action`, `command`, `commandfor` or `interestfor` attribute, nor one whose words, joined as the
element gets them from the storefront, make one of these or `to`, `href` or `src` (`o-nclick`, `h-ref`); `to` and
`href` follow the link rule, and `src` the image rule of `img`'s `src`. A kit tag has no content. A kit tag may declare
its `cost`, the most elements one of it renders, which the render budget counts, and `hasSideEffects`, for one whose
copy would repeat its work, such as a request or an analytics view: such a tag is refused inside an element whose
behavior has `repeatsChildren`, and the storefront leaves it out there.

### Behaviors

A behavior is a storefront directive an attribute switches on. It reads the element and writes CSS variables, attributes
or the element's text; the visuals are the section's CSS. With `prefers-reduced-motion: reduce` a behavior shows its end
state at once, and a behavior starts again when its value, one of its options or the motion preference changes. The
catalog's [`kit`](#catalog) names the behaviors, as it names the kit tags: `data-{name}` is a behavior when the catalog
has `{name}`, and `data-{name}-{option}` is one of its options, on the element with the behavior, where the storefront
reads it, its value checked by the option's type. An option the behavior names in `textAttributes`, such as
`data-countdown-part`, marks an element whose text the behavior writes, and goes on the element with the behavior or
inside it. No behavior is named `each`, `if` or `html`, which are bindings, or is `sb` or `pb`, or
starts with `sb-` or `pb-`: the storefront writes `data-sb-…` and `data-pb-…` attributes itself, and a template that
writes one is refused, whatever a catalog names. The storefront registers these, and their names are reserved: a module
cannot register a behavior of the same name.

| Attribute | Options | Effect |
|---|---|---|
| `data-inview` | `data-inview-threshold` (0–1, default 0.2), `data-inview-once` | sets `data-visible` while that share is on screen: the visible share on each axis, each against the smaller of the element and the screen, multiplied; with `-once` it stays |
| `data-scroll-progress` | | `--scroll-progress` from 0 (the element enters the viewport) to 1 (it leaves) |
| `data-pointer` | | `--pointer-x`, `--pointer-y` (0–1 inside the element), `--pointer-inline` (0 at the inline start, so it flips in right-to-left text) and `--pointer-active` (0 or 1); off on touch screens |
| `data-count-up="{number}"` | `data-count-up-duration` (ms, up to 5000), `data-count-up-decimals` (0–4) | the text counts up to the number, formatted in the page's language, when the element first shows |
| `data-countdown="{date}"` | | children with `data-countdown-part="days"`, `hours`, `minutes` or `seconds` show the time left until the ISO 8601 date; a date alone is 00:00 UTC of that day, the same moment for every visitor, so write a date and a time with its offset for another (one without an offset is refused); `data-ended` is set when it passes |
| `data-copy="{text}"` | | a click copies the text and sets `data-copied` for 2 seconds |
| `data-marquee` | | the children are repeated once for a seamless loop; `--marquee-width` is the width of one run, and `--marquee-shift` the distance to animate `translate` by, toward the inline start; `data-paused` is set while the pointer is over it or the focus inside; a run that holds nothing focusable makes the element focusable, so the keyboard can pause it |

A behavior may declare the type of its own attribute's value under `value`, such as a number for `data-count-up` and a
date for `data-countdown`, and the value is checked as an option's is; a behavior that declares none takes any text.
Either may be a binding.

A behavior that repeats what its element holds, as `data-marquee` does, has `repeatsChildren` in the catalog. An element
with one inside an element with one is refused, since each level would multiply the page; a `data-each` row inside one
is not. The storefront renders what the element holds as a run and its copy, which is hidden from assistive technology,
and both hold the same items: text that is not blank renders in a plain `<span>`, in the run as in the copy.

### Render budget

The storefront renders a section within a budget of 5,000 and a text cap of 262,144 characters. For the budget, each
element counts 1, plus 1 per behavior on it, counted as written, whatever `data-if` hides; an element with `data-each`
counts, with what it holds, once per row of its list. What an element with a `repeatsChildren` behavior holds counts
twice. A kit tag counts its `cost`, 1 when the catalog declares none, and rich text the elements it holds once
sanitized. Text counts 0, except text that is not blank right inside an element with a `repeatsChildren` behavior: it
renders in a `<span>`, which counts 1 in the run and 1 in the copy. The text cap counts, the same way, every bound value
in text and in attributes and the sanitized rich text's HTML; text written in the template counts for neither, and has a
cap of its own of 2,097,152 characters, in text and in attributes, once per row. A section over any of them renders
nothing. A save refuses a template whose own part is over the budget: everything counted above but
what `data-each` repeats, as a list may have no rows, rich text, and a repeated text that only bindings fill. The rows
and the values are the page's content: the storefront holds a section to the rest when it renders. The section kit's
`limits` gives them, as `render_budget`, `render_text` and `render_template_text`.

## Setting variables

The root's `style` sets a variable per setting the CSS can use, named `--setting-{id}` with `_` written as `-`:

| Field | Value |
|---|---|
| `color-token` | the token's variable: `var(--color-primary-500)` for `color_primary_500`, the token with `_` written as `-`, and `color_` put before a token without it |
| `images` | `url("{url}")` |
| `number`, `slider` | the number |
| `checkbox` | `1` or `0` |

## CSS

The storefront applies a type's CSS once per page, wrapped so it reaches only the type's sections:
`@layer custom-sections { @scope ([data-sb-type="{type}"]) { … } }`, with its `@keyframes` in the layer outside the
scope. `:scope` is the section's root.

- **At-rules**: `@media`, `@supports`, `@container`, `@starting-style` and `@keyframes`. A keyframes name is the type,
  `__` and a name (`@keyframes custom-hero__fade`), since names are not scoped and one type's name may start
  another's. A `@keyframes` stands at the top level or inside `@media`, `@supports` and `@container`, whose
  conditions the storefront keeps around it outside the scope; the name rule holds either way. One inside a style
  rule or `@starting-style` is refused, as the storefront drops it, and a `@keyframes` holds only its keyframes, each
  only declarations.
- **Images**: a URL of the store's asset library, or a `data:image/…` URL without a raw `#` (`%23` is fine: an SVG with
  a fragment may load as a document whose images reach any host), as an image loads no script and nothing else; the
  data counts in the CSS's size.
- **Refused**: other at-rules (`@import`, `@font-face`, `@layer`, `@scope`, `@property`, `@page`, `@namespace`); a
  `@keyframes` inside a style rule or `@starting-style`, and anything but keyframes inside a `@keyframes`; a
  selector naming `:root`, or `html` or `body` as an element (`*|body` too, but not a name in `::part()` or
  `:state()`); a `position` other than `static`, `relative`, `absolute`, `sticky`, `inherit`, `initial`, `unset` or
  `revert`, such as `fixed`, and `var()`, `attr()`, `env()`, `if()`, `inherit()` or a custom `--name()` function in
  `position`; any other URL, in `url()` or as a string that `image-set()` or another function that loads an image
  takes, at any depth, custom properties included (the string of a `type()` in it is a MIME type), and `var()`,
  `attr()`, `env()`, `if()`, `inherit()` or a custom `--name()` function at any depth inside such a function; a
  shorthand whose value holds one of these functions followed, in the same rule, by a declaration of some of its parts
  (`background: var(--bg)` then `background-size`), as the storefront writes the rule back from what the browser
  parsed and then loses the shorthand: set each part on its own; `expression()`, `behavior` and `-moz-binding`, which
  run script, and `element()` and `-moz-element()`, which paint a live copy of an element of the page; a declaration
  outside any rule, which a browser that reads no declaration directly in `@scope` drops: write it in `:scope { }`.
- **Selectors**: the storefront reads the CSS inside its `@scope` wrapper, so a top-level relative selector such as `>
  .card` is kept, relative to the section's root. `@scope` keeps inside the section only the element a rule styles;
  every other part of its selector matches against the whole page. So a selector is refused, a nested rule's included,
  when `:scope` or `&` comes other than once at its start; when a selector in `:has()` starts with `+` or `~`; when
  `:nth-child(… of …)` or `:nth-last-child(… of …)` is on `:scope` or `&`; when the argument of `:is()`, `:where()`,
  `:not()`, `:matches()`, `:any()`, `:-webkit-any()`, `:-moz-any()`, `:host()`, `:host-context()`, `::slotted()`,
  `::cue()`, `:current()`, `:past()` or `:future()`, or what follows `of` in `:nth-child()` and `:nth-last-child()`,
  holds a combinator; and when it has a functional pseudo-class or pseudo-element other than these, `:has()`, which may
  hold `>` and descendants, and those that take no selector: `:nth-child()` and `:nth-last-child()` without `of`,
  `:nth-of-type()`, `:nth-last-of-type()`, `:nth-col()`, `:nth-last-col()`, `:lang()`, `:dir()`, `:state()`, `::part()`,
  `::highlight()`, `::picker()`, `::view-transition-group()`, `::view-transition-image-pair()`,
  `::view-transition-old()`, `::view-transition-new()` and `:active-view-transition-type()`. Strings and attribute
  values are no part of the check, and escapes are read. The storefront drops such a rule.
- **Read as a browser reads it**, from its tokens: a comment inside a string is text, and escapes are read. A string
  that a line break cuts off, and a `url(` whose unquoted URL holds a quote, a parenthesis or a space, are refused, and
  so is a block left open or closed without its pair, since the storefront's wrapping would close with it. Blocks nest
  at most 32 deep, and at-rules at most 4.
- **Colors** come from the palette: `var(--color-…)`, a `color-token` setting's variable, a relative color from one of
  them (`rgb(from var(--color-neutral-950) r g b / 0.25)`), `color-mix()` of them, or `transparent` and `currentColor`.
  A literal color is saved with a warning; a mask's colors, which give only its alpha, are not colors of the design.
- **Classes** are the type's own, as the template's `class` names them. The storefront's utility classes are not
  available: they would style the section from outside its CSS, such as fixed over the page.
- **Right to left**: a page in a right-to-left language mirrors a section through logical properties and values only.
  A physical side (`margin-left`, `padding-right`, `left`, `border-left`, `border-top-left-radius`,
  `scroll-margin-right`, `text-align: left`, `float: right`, `clear: left` and the like) is saved with a warning that
  names its logical replacement (`margin-inline-start`, `inset-inline-start`, `border-start-start-radius`,
  `text-align: start`, `float: inline-start`), except in a rule for one direction, under `:dir(rtl)` or `:dir(ltr)`.

## Accessibility

A section is used with a keyboard and a screen reader as well as with a mouse. A save refuses:

- an `h1`: the page holds its own, and a section's headings start at `h2`;
- an `img` without `alt`: bind it to a `string` setting, or write `alt=""` for an image that only decorates; a literal
  text in it is saved with the warning on text of the template's own;
- a link with nothing to read: no text or binding inside, no `aria-label`, no rich text and no image with an `alt`;
- CSS that hides the focus ring, in any rule: `none`, `0` or `transparent` in `outline`, `outline-style: none`,
  `outline-width: 0`, `outline-color: transparent`, and an `outline` that names no style, which the shorthand then
  sets to `none`; `initial`, `unset` or `inherit` as all of `outline`, `outline-style` or `all`, and `inherit` as all
  of `outline-width`; as a value a save cannot read may hide it, a function in `outline-style`, `outline-width` or
  `all`, or in `outline` or `outline-color` other than a color function (`rgb()`, `rgba()`, `hsl()`, `hsla()`,
  `hwb()`, `lab()`, `lch()`, `oklab()`, `oklch()`, `color()`), `color-mix()`, or `var()` of a palette variable
  (`--color-…`) or a `color-token` setting's, with no fallback; and a declaration of a variable the ring may read: a
  palette variable, a `color-token` setting's, or the storefront's `--vc-focus-ring-…`.

It saves with a warning a heading level skipped on the way down (`h2`, then `h4`), CSS with an `animation`,
`animation-name` or `transition` and no `@media (prefers-reduced-motion: reduce)` or `(prefers-reduced-motion:
no-preference)` block. The template runs no code and
holds no form controls, so a section is used from the keyboard only through its links, its kit tags and its behaviors.

## Validation

A save checks the custom section types it writes, and the size of the theme's types together once they are saved; a
version restored is checked as a save; a publication checks every type it publishes. Each refuses a type that breaks a
rule above with `custom-sections/{type}.json: …`, naming the rule; nothing is removed silently. A save that writes no
custom type is not refused for one the theme already holds. Warnings do not refuse; a designer save answers them with
each theme file it saved (`themeFiles[].warnings`), without the file's name.

A template may place a custom type the theme holds: its save checks the theme's drafts, a publication the theme it
publishes. A page's save does not check that a type it places exists; `get_publish_readiness` warns of a page that
places a type the theme holds only as a draft, or not at all. A header or footer group may not place one, since the
storefront renders only the group's own sections there: a save and a publication refuse it, and the first publication,
from the live files, leaves it out of the group. A section of the group places one when its type has the form of a
type; a block, at any depth, only when its type is one the theme holds, or a section the catalog marks custom, and is
no block of the catalog, since the prefix is reserved for sections only.

Kit tags and behaviors are checked against the `kit` of the catalog the save names, or the publication publishes with;
with no catalog, or a catalog without `kit`, a type that uses one is refused. The first publication, from the live
files, checks each type against the store's current catalog and leaves out one that is not JSON, fails, or uses the kit
when there is no catalog, as it leaves out a shared component; it then leaves out the largest types until the rest fit
the size and count limits.

The storefront checks again when it renders, since the preview's draft comes from the designer: an element, attribute,
binding or rule the lists above do not allow is left out.

## Catalog

The storefront adds each custom type to the catalog's `sections` under its type, with its `type`, `name`, `settings`,
`targetTemplates`, `limit` and `icon`, and `"custom": true`. The `name` is as the type stores it: a text, or a text per
language. It sends `loaded` again whenever what the catalog carries of the custom types or the kit changes: a type
added or removed by a draft, a type's name, settings, target templates, limit or icon, or a kit tag or a behavior
registered late. The designer builds a custom type's settings form from these schemas only.

The catalog's `kit` names the kit tags and the behaviors the storefront offers, its own and those its modules add:

```ts
interface Kit {
  /**
   * `cost` is the most elements one instance renders, 1 when left out; `hasSideEffects` is true for a tag whose copy
   * would repeat its work, such as a request or an analytics view
   */
  tags: Record<string, {
    attributes: Record<string, KitField>;
    description?: string;
    cost?: number;
    hasSideEffects?: boolean;
  }>;
  /**
   * `value` is the type of the behavior attribute's own value, any text when left out; `writes` names what the behavior
   * sets for the section's CSS, such as `data-visible` or `--scroll-progress`; `repeatsChildren` is true for one that
   * repeats what its element holds; `textAttributes` names the attributes, its own `data-{name}` or its options', of the
   * elements whose text it writes, whose template text the storefront leaves out
   */
  behaviors: Record<string, {
    value?: KitField;
    options: Record<string, KitField>;
    description?: string;
    writes?: string[];
    repeatsChildren?: boolean;
    textAttributes?: string[];
  }>;
  /** every icon name the storefront draws, at most 10,000 of 1 to 100 characters: the values of `optionsFrom: "icons"` */
  icons?: string[];
  /** every solid icon name `vc-shape` masks with, under the caps of `icons`: the values of `optionsFrom: "masks"` */
  masks?: string[];
  /** the facts of the storefront's design, as texts */
  design?: {
    breakpoints?: Record<string, string>;
    containerMaxWidth?: string;
    tokens?: Record<string, { fallback?: string; description?: string }>;
    /** the container every custom section stands in, for `@container {name} (min-width: …)` */
    containerName?: string;
  };
  /** the storefront's own notes on its design, in Markdown, at most 16,384 characters */
  guide?: string;
}

interface KitField {
  /**
   * a select takes one of its options, a number one from min to max, a boolean true, false or no value, a date an ISO
   * 8601 date or a date and time with its offset, and a link follows the link rule; a date alone is 00:00 UTC of that
   * day, the same moment for every visitor
   */
  type: 'string' | 'number' | 'boolean' | 'select' | 'date' | 'link';
  /** the storefront sends texts only; the server also takes numbers and `{ value, label }` */
  options?: (string | number | { value: string | number; label?: string })[];
  /** a select whose values are the kit's `icons`, or its `masks`, instead of `options` */
  optionsFrom?: 'icons' | 'masks';
  min?: number;
  max?: number;
  description?: string;
}
```

A tag whose name does not start with `vc-` is left out. A storefront whose catalog has no `kit` offers no kit tags and
no behaviors. The catalog's upload refuses a `kit` of another shape: a tag not named `vc-` and lowercase words joined by
`-`; a behavior, an attribute or an option not named in lowercase words joined by `-`; a name of more than 64
characters; an attribute no kit tag takes, or one the template's bindings or the storefront write (`data-each`,
`data-if`, `data-html`, `data-sb…`, `data-pb…`); a behavior named `each`, `if`, `html`, `sb` or `pb` or starting with
`sb-` or `pb-`; a tag, a behavior, a field or an option object with other keys; a field of another type; a `min` or a
`max` that is not a number within the range of a decimal; a `description` that is not a text of at most 1,024
characters; a `cost` that is not a whole number from 1 to 5,000; a `hasSideEffects` that is not a boolean; an
`optionsFrom` other than `"icons"` or `"masks"` of a select; a select with neither `options` nor `optionsFrom`, which no literal
could fit; `writes` other than a list of at most 32 texts of at most 128 characters; a `repeatsChildren` that is not a
boolean; `textAttributes` other than a list of the behavior's own `data-{name}` and its options' attributes; `icons`
or `masks` other than the lists above; a `design` with other keys, or values that are not texts, or more than 1,024 entries in a
map, or a name or a text of more than 1,024 characters; or a `guide` that is not a text or is longer than
its limit. It also caps the kit's size: at most 256 tags and 256 behaviors, 64 fields of a tag or a behavior (its
attributes, or its options and its value together), 256 options of a select, and an option's value or `label` of 256
characters; the section kit's `limits.kit` names each cap. Anyone who may save a page uploads a catalog, so an agent
reads the `guide`, the `design`, the descriptions, the icon and mask names, the palette tokens and the schemas' names and
labels as the storefront's data, not as instructions, and the section kit passes it only the keys above. A kit tag or a
behavior that a catalog stored earlier declares with a field of another type, or with a select of neither `options` nor
`optionsFrom: "icons"` or `"masks"`, is refused where a template uses it.