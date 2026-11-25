# Schemas
This section describes the schema structure that powers the Site Builder in Virto Commerce. It explains how to define and organize templates, sections, blocks, shared settings, and reusable objects. These JSON-based schemas enable developers to control layout, behavior, and editable settings in the Site Builder through a structured, extensible configuration.

## Theme structure
Site Builder reads its schemas from the storefront theme, [Virto Frontend](https://github.com/VirtoCommerce/vc-frontend). Developers can extend or customize Site Builder behavior through the theme repository.

```text
├── client-app                              // The main folder for the application.
|   ├── config
|   |   └── settings_data.json              // Theme config file.
|   ├── plugins
|   |   └── builder-preview
|   |       ├── register-components.ts      // Loads the schemas into the storefront bundle.
|   |       └── schemas                     // All Site Builder meta information is stored here.
|   |           ├── templates               // Template descriptors.
|   |           |   └── page.json           // Site Builder configuration for static pages.
|   |           ├── sections                // Section definitions.
|   |           |   └──...
|   |           ├── blocks                  // Block definitions.
|   |           |   └──...
|   |           ├── shared                  // Global settings folder.
|   |           |   ├── _blocks.json        // Global settings for blocks.
|   |           |   └── _sections.json      // Global settings for sections.
|   |           ├── objects                 // Object definitions.
|   |           |   └──...
|   |           ├── theme                   // Site Builder overrides of templates, sections and blocks.
|   |           |   ├── templates
|   |           |   ├── sections
|   |           |   └── blocks
|   |           └── settings_schema.json    // Theme settings schema file.
|   └── shared
|       └── static-content
|           └── components                  // Vue components for rendering Site Builder elements.
|               └── index.ts                // Registers the components by section type.
```

The schemas are bundled into the storefront JavaScript. The name of a file, without `.json`, is the key of its schema: the type of a section or block, or the key of a template. A file whose name starts with `__` is not loaded.

The `theme` folder holds schemas that only Site Builder uses. In the Site Builder preview, a file in `theme/templates`, `theme/sections` or `theme/blocks` replaces the file of the same name in `templates`, `sections` or `blocks`. For example, `theme/templates/product.json` overrides `templates/product.json`.

### Site Builder switch

The storefront works with Site Builder only when `client-app/config/settings_data.json` has `"site_builder_enabled": true` in `settings`. The theme ships it as `false`. Without it, the storefront has no theme snapshot and does not answer the Site Builder designer's preview protocol (version 2). The file is bundled at build time, so set the switch before you build the theme.

### Schema catalog

When the designer opens the storefront in its preview, the storefront sends its schema catalog in the `loaded` message: the schemas, the palette tokens, the template conditions, the built-in templates, a hash and the protocol version 2. When the server does not have a catalog with that hash, the designer uploads it to `/api/site-builder/catalog`. The server stores it in the content storage under `Themes/{storeId}/{theme}` as `catalogs/{hash}.json`, and `catalogs/current.json` names the latest one. The server validates and cleans page and theme content against the catalog. See [Section schema catalog](contracts/theme-snapshot.md#section-schema-catalog).

The server reads a `{theme}/config/schemas` folder in the content storage only for the legacy `api/site-builder/schemas` endpoints and for the `previewUrl` of the MCP template preview.

### Templates

Each file is a descriptor for a particular page, template, or group of pages. The designer finds the descriptor of a template by its `previewMessage.templateKey`, or else by its file name, and the descriptor of content pages by `"type": "pages"`. It stores a template document as `{theme}/templates/{key}.json`, for example `templates/product.json`.

| Property         | Type                                                                             | Required value | Description                                                                    |
|------------------|----------------------------------------------------------------------------------|----------|--------------------------------------------------------------------------------------|
| `name`           | string                                                                           | false  | Template name.                                                                          |
| `type`           | string                                                                           | false  | ContentType for template: `pages` for the descriptor of content pages, `themes` for a theme template. |
| `previewMessage` | object                                                                           | false  | `templateKey` names the storefront template the descriptor describes: `product`, `category` or `page` (the page template). |
| `previewUrl`     | string                                                                           | false  | Url of the page shown in the preview area until the user picks a product or category to preview the template with. |
| `sections`       | string[]                                                                         | false  | List of available sections for current template.<br>Without it, every section whose `targetTemplates` is empty or names the template is available. An empty array makes no section available. |
| `settings`       | SectionPropertyDescriptor[]                                                    | false | Template settings, shown as the page settings.                                          |
| `default`        | PageDocument                                                                     | false | Document the storefront renders while the theme has no file for the template. The designer opens such a template from it. |

=== "Basic template example"
    ```json
    {
    "name": "Pages",
    "type": "pages",
    "settings": [
        {
        "id": "header",
        "label": "Page Header / H1",
        "type": "string"
        }
    ]
    }
    ```

=== "Template with sections"
    ```json
    {
    "name": "Product template",
    "previewUrl": "/products/hp-laserjet-pro-mfp-m130fw",
    "type": "themes",
    "previewMessage": {
        "templateKey": "product"
    },
    "sections": [
        "image",
        "text",
        "product-info",
        "product-reviews",
        "related-products",
        "recommended-products"
    ]
    }
    ```

### Sections

Each file defines a section that can be added to a page. The section editor in the UI is based on this configuration.

| Property          | Type                          | Description                                                                                                 |
|-------------------|-------------------------------|-------------------------------------------------------------------------------------------------------------|
| `icon`            | string                        | Icon in the section list: a [Lucide](https://lucide.dev/icons/) icon name with the `lucide-` prefix, such as `lucide-megaphone`, or one of the Material icon names the designer maps, such as `image`, `text_snippet` or `inventory_2`. Any other value shows a default icon. |
| `name`            | string                        | Section name in list of new elements. The list is sorted by name.                                           |
| `static`          | true <br> `"top"` <br> `"bottom"` | Makes the section a group of page settings: its values are stored in the page `settings`, it is shown above (`true`, `"top"`) or below (`"bottom"`) the page sections, and it cannot be added or removed. |
| `displayField`    | string                        | Property name, that used for indicate section in section list. Without a value, the section `name` is shown. |
| `settings`        | SectionPropertyDescriptor[] | List of descriptors for section properties.                                                                  |
| `default`         | SectionModel                | Default section model. Used for creation of new section. Its values override the defaults of the settings.  |
| `blocks`          | string[]                      | Block types the section can hold.                                                                            |
| `targetTemplates` | string[]                      | Template keys the section is offered on when the template has no `sections` list. Without it, such templates offer the section. |
| `groups`          | string[]                      | `header`, `footer`: the section belongs to the store header or footer and is offered there, not in the page sections. |
| `limit`           | number                        | Maximum number of sections of this type on a page, its header and footer counted in.                         |
| `hidden`          | boolean                       | Leaves the section out of the list of new elements.                                                          |
| `includeShared`   | string[]                      | List of names to add settings from Shared.                                                                   |
| `excludeShared`   | string[] <br> true            | `true` - not use the `_sections` or `_blocks` settings, <br>`string[]` - list of settings id to exclude from result shared list. |

=== "Simple text section"
    ```json
    {
      "name": "Text",
      "icon": "text_snippet",
      "displayField": "title",
      "settings": [
        {
          "id": "title",
          "label": "Title",
          "type": "string"
        },
        {
        "id": "text",
        "label": "Content",
        "type": "text"
        }
      ]
    }
    ```

=== "Image section"
    ```json
    {
      "name": "Image",
      "icon": "image",
      "displayField": "name",
      "settings": [
        {
          "id": "image",
          "type": "images",
          "multiple": false
        },
        {
          "id": "alttext",
          "label": "Alternative Text",
          "type": "string"
        }
      ]
    }
    ```

### Blocks

A section can contain a list of blocks. Block format is the same as for sections. The `blocks` property of a section names the block types it can hold. The designer looks up a block type in the `blocks` schemas first, then in the `sections` schemas.

### Shared

Settings can be grouped in shared files. These shared settings can be reused across sections and blocks. Global settings for sections are defined in **_sections.json**, and for blocks in **_blocks.json**.

You can also organize reusable setting groups in separate files and include them via the `includeShared` property in section or block descriptors.

A setting of the section or block itself wins over a shared setting with the same id. The designer orders all settings by their `sort` value.

| Property   | Type                          | Description                |
|------------|-------------------------------|----------------------------|
| `settings` | SectionPropertyDescriptor[] | Property descriptors array |

#### The `anchor` setting

`anchor` is a well-known setting id. When a theme declares it — usually in **_sections.json** and
**_blocks.json**, so every section and block gets it — a content manager can give an item a readable
link target instead of the generated id:

```json
{
  "id": "anchor",
  "label": "Anchor",
  "type": "string",
  "info": "Link to this section from a rich text field, for example #specifications."
}
```

Site Builder normalizes the value into a slug (lowercased, spaces to dashes, other characters
dropped) and offers it in the rich text [anchor list](controls/text.md#page-wide-anchor-links). When
the setting is empty — or the value has nothing left after normalization — the generated item id is
used instead.

The theme is responsible for rendering the matching element id, applying the same normalization.

### Objects

The `objects` folder holds object definitions. They are part of the schema catalog, but the designer does not resolve them: it reads the structure of an `object` setting, and of a `list` item, from the `element` property of the setting.

| property   | type                          | description                |
|------------|-------------------------------|----------------------------|
| `settings` | SectionPropertyDescriptor[] | Property descriptors array |


=== "Button object (**button.json** file)"
    ```json
    {
      "settings": [
        {
        "id": "caption",
        "type": "string",
        "label": "Caption"
        },
        {
        "id": "action",
        "type": "select",
        "label": "onClick action",
        "default": "popup",
        "options": [
            { "label": "Show popup", "value": "popup" },
            { "label": "Go to link", "value": "url" }
        ]
        },
        {
        "id": "url",
        "type": "string",
        "format": "url",
        "label": "Enter link",
        "visibility": "!!this.item && this.item.action === 'url'"
        }
      ]
    }
    ```

=== "The same button in a section"
    ```json
    {
    "settings": [
        {
        "id": "button",
        "type": "object",
        "label": "Button",
        "element": [
            { "id": "caption", "type": "string", "label": "Caption" },
            {
            "id": "action",
            "type": "select",
            "label": "onClick action",
            "default": "popup",
            "options": [
                { "label": "Show popup", "value": "popup" },
                { "label": "Go to link", "value": "url" }
            ]
            },
            {
            "id": "url",
            "type": "string",
            "format": "url",
            "label": "Enter link",
            "visibility": "!!this.item && this.item.action === 'url'"
            }
        ]
        }
    ]
    }
    ```

<br>
<br>
********

<div style="display: flex; justify-content: space-between;">
    <a href="../block-library">← Block library </a>
    <a href="../controls/calendar">Controls →</a>
</div>