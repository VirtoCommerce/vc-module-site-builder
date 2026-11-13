# Site Builder (Beta)

> **Beta.** Site Builder is ready to try and its storage stays compatible with Page Builder, but its APIs and theme formats may still change before the first stable release.

Site Builder lets a merchandiser build the storefront's pages, catalog templates and theme in a visual designer, next to a live preview of the real storefront.

It replaces Page Builder ([vc-module-pagebuilder](https://github.com/VirtoCommerce/vc-module-pagebuilder)). The two cannot be installed together, and Site Builder uses Page Builder's database as it is, so existing pages stay after the switch.

![A landing page in the Site Builder designer](docs/media/site-builder-designer.png)

## What it does
* **Pages from sections.** Landing pages, content pages and blog articles are built from sections: add, reorder, copy, paste and hide them, and edit their settings while the preview updates.
* **Catalog templates and rules.** Product, category and other storefront templates are edited the same way. Template rules decide which template a page gets, by category, product type or property, and the rule panel shows which rule a previewed product matches.
* **Theme.** Header, footer, light and dark palettes, logos and favicon. The palette generator builds both palettes from one or two brand colors, with every text and background pair at WCAG 2.2 AA contrast.
* **Theme publishing.** One action puts every theme draft live as a new versioned theme per language. A publication that fails validation leaves the previous one live.
* **Multilanguage pages,** with per-language values for translatable fields.
* **Version history on GitHub.** Each store can have its own repository: every save is a commit, any version can be restored, and pages can go live through a pull request.
* **AI assistant (Virto OZ)** generates, edits, diagnoses and bulk-updates pages from a text prompt. See [AI Assistant for Site Builder](docs/ai-assistant.md).
* **Admin Shell plugin and a standalone designer app.**

The store setting **New Site Builder** switches a store to the new designer; with it off, the store keeps the classic designer.

![A product template with its template rules](docs/media/site-builder-template-rules.png)

## Switching from Page Builder

On its first start, Site Builder copies Page Builder's settings to its own names in a background job:

* the global settings `StoreUrl`, `StorePreviewPath` and `OzAgentUrl`;
* each store's `PreviewUserIds`, `GitContentEnabled`, `GitContentRepository`, and `NewPageBuilder` as `NewSiteBuilder`, so a store on the new designer stays on it;
* the saved GitHub App connection, encrypted again for Site Builder.

A setting that already has a stored Site Builder value keeps it. The copy runs once: if you later switch back to Page Builder and then to Site Builder again, Site Builder keeps its own values. If this server cannot decrypt the GitHub App connection (its data protection keys were not kept), the log has a warning naming the setting, and the connection has to be set up again.

The `PageBuilder:GitContent` and `PageBuilder:ThemeSnapshot` configuration sections (`PageBuilder__…` environment variables included) and the `VirtoCommerce.PageBuilderModule` connection string still work when the Site Builder name is not set. They are deprecated, and a warning at startup lists them: rename them to `SiteBuilder:…` and `VirtoCommerce.SiteBuilder`.

Roles keep working: the permissions are the same `builder:*` ones.

Settings forced in configuration under `VirtoCommerce:Settings:Override` need a manual step: they are keyed by setting name, so rename `VirtoCommerce.PageBuilderModule.*` to `VirtoCommerce.SiteBuilder.*`, and `NewPageBuilder` to `NewSiteBuilder`.

## Store access

A role can hold `builder:read` and `builder:shared-components:read` for selected stores only (the **Only for selected stores** scope). Every Site Builder endpoint checks the store it addresses: besides its own permission, the user needs the read permission in that store, for every store or through the scope. An account bound to a store reaches that store only.

The Vue designer lists a theme's alternate templates through the Content module's theme search (`GET api/content/themes/{storeId}/search`), so switching to one needs `content:read` as well; without it the template switcher offers only the default templates and says why.

## AI agents (MCP)

The module adds a Site Builder toolset to the back-office MCP endpoint of the [MCP module](https://github.com/VirtoCommerce/vc-module-mcp), `/admin/mcp`. It is for two kinds of work:

* **The content author**, a marketer or content manager: landing pages, content pages, translations, SEO, and who sees a page.
* **The site designer**, who owns how the site looks: product, category and page templates, the rules that choose a template, header, footer, brand and palette.

They ask an AI agent (Claude, ChatGPT, VS Code or any MCP client) in plain words, and the agent works through the same checks as the designer: the store access of the user, page and theme validation, drafts and version history. Customers see nothing an agent publishes until a second person approves it.

### Features

- **Orientation.** One call tells what a store's site holds: pages by status and language, what waits for publication, recent changes with who made them, translation coverage, the theme's drafts, and whether history and the schema catalog are available.
- **Pages from a request.** Create a landing page with its sections, or as a copy of another page; edit sections, blocks, texts, SEO and layout of a draft; place and detach Shared Components.
- **Languages.** Turn a landing page into a Multilanguage Page, link and unlink language versions, create the missing ones and fill them with translations bound to the version of the source they were made from.
- **Content gaps.** Missing and outdated translations and pages without an SEO title or description, as findings with evidence and a fix.
- **Bulk changes.** One change of a section on every page that matches a filter, and translations of many pages, run as background jobs.
- **Templates and theme.** Create and edit templates, reorder, enable and disable template rules, edit the header, footer and brand, and generate the light and dark palette from brand colors with a contrast check.
- **Before publishing.** What a publication would put live, who changed each part, what would refuse it, and who would see it, from the dry run of the publication itself.
- **Safe changes.** Every change has a dry run. Publishing a page or the theme, and changing what a published page shows or who sees it, wait for a second person's approval. Restoring a version and discarding a draft ask the user to confirm.

### Quick setup

For the administrator, once:

1. **Install the modules.** Site Builder and the [MCP module](https://github.com/VirtoCommerce/vc-module-mcp), on platform `3.1096.0` or later. The MCP dependency is optional: without the MCP module, Site Builder works as before and the toolset is not served.
2. **Install Background Jobs** (`VirtoCommerce.BackgroundJobs`) for the bulk tools, `update_sections_bulk` and `set_translations`. Without it they answer that jobs are not available and change nothing; every other tool works.
3. **Open the endpoint for agents.** Add `https://<platform>/admin/mcp` to `Authorization:Resources` and register the agent in **Security → OAuth applications**, as the MCP module's [Set up the platform for agents](https://github.com/VirtoCommerce/vc-module-mcp#set-up-the-platform-for-agents) describes. Skip this step if agents already connect to `/admin/mcp`.
4. **Grant the permissions.** Give the content author and the site designer roles with the permissions their work needs. Every tool needs `builder:read` in the store it works in. `builder:read` and `builder:shared-components:read` may be limited to some stores (**Only for selected stores**); the agent then reads and changes those stores only, as in the designer.

   | Permission | Gives |
   | --- | --- |
   | `builder:read` | Every read tool, the prompts, and the schema and rule-condition resources, in the stores it covers |
   | `builder:update` | Editing page drafts, page settings, language links, translations, bulk changes, restoring versions and discarding drafts |
   | `builder:create` | `create_page`; with `builder:update`, `convert_page_to_multilanguage` |
   | `builder:publish` | `publish_page`, `unpublish_page` and `publish_theme`, as requests for approval |
   | `builder:templates` | With `builder:update`: `edit_template` and `edit_template_rules` |
   | `builder:theme` | With `builder:update`: `edit_theme_draft` |
   | `builder:shared-components:read`, `:create`, `:update` | `search_shared_components`; with it, `create_shared_component` and `revise_shared_component` |
   | `platform:asset:read` | `search_assets` |
   | `platform:setting:read` | The store settings and user groups resources |
   | `customer:read` | The organizations resource, granted for every organization |
   | `mcp:approve` | Approving publications and changes of published pages in **Agent approvals** |

5. **Keep approvers apart.** Give `mcp:approve` to another role than the people who ask for changes. For a pilot with one person, the MCP module's setting `MCP.Approval.AllowSelfApproval` lets the requester approve their own changes; switch it off afterwards.

For each user: connect the agent to `https://<platform>/admin/mcp` with the client id from the administrator, as the MCP module's [Connect an AI agent](https://github.com/VirtoCommerce/vc-module-mcp#connect-an-ai-agent) describes, and sign in with your back-office account. The agent works as you and sees only the tools your role allows.

### Tools

| Layer | Tool | What it does | Permission | Safeguard |
| --- | --- | --- | --- | --- |
| Orientation | `get_site_overview` | Start here: pages, drafts, recent changes, language coverage, theme state, history, schema catalog and publication flow of a store or of every store you read | `builder:read` | — |
| Search | `search_site_pages` | Pages from the database and, per store and folder, pages stored as files: the list, one page with its sections and version token, or the pages that use a section type | `builder:read` | — |
| Search | `search_templates` | Templates, their drafts, the rules that give them pages, and every rule in order | `builder:read` | — |
| Search | `search_assets` | Images and files of a folder, and where they are used | `platform:asset:read` | — |
| Search | `search_shared_components` | Shared Components, their content and the pages they are placed on | `builder:shared-components:read` | — |
| Search | `list_versions` | Saved versions of a page or of theme files, from the store's GitHub history; the commits of a page stored as a file in git | `builder:read` | — |
| Analysis | `get_preview` | A storefront link to a draft or a version, and which template and rule a page, product or category gets | `builder:read` | — |
| Analysis | `find_content_gaps` | Missing and outdated translations, pages without SEO title or description | `builder:read` | — |
| Analysis | `get_publish_readiness` | What a page or theme publication would put live, who changed it, what blocks it, who sees it | `builder:read` | — |
| Content | `create_page` | A landing page with sections or as a copy, a missing language version, a copy of a Multilanguage Page | `builder:create` | Dry run |
| Content | `edit_page_draft` | Sections, blocks, texts, SEO, layout and Shared Components of a draft | `builder:update` | Dry run, version token |
| Content | `change_page_settings` | Name, language, address, audience and dates | `builder:update` | Approval; none with `unpublished_only` for a page never published |
| Content | `convert_page_to_multilanguage` | A landing page becomes a Multilanguage Page | `builder:create`, `builder:update` | Approval; none with `unpublished_only` for a page never published |
| Content | `link_page_language`, `unlink_page_language` | A page becomes a language version, or a landing page again | `builder:update` | Approval; none with `unpublished_only` for a page never published |
| Content | `reset_language_overrides` | Language versions take the values of the Multilanguage Page again | `builder:update` | Approval; none with `unpublished_only` when no version is published |
| Content | `publish_page`, `unpublish_page` | Publish a draft, or take a page off the storefront | `builder:publish` | Approval |
| Content | `restore_version` | A saved version of a page or of theme files becomes the draft | `builder:update` and the part's permission | Confirmation, expected versions |
| Content | `discard_draft` | Drop the draft of a page or of a theme file | `builder:update` and the part's permission | Confirmation |
| Bulk | `set_translations` | Translations of many pages, bound to the source version | `builder:update` | Background job, dry run |
| Bulk | `update_sections_bulk` | One change of a section on every page of a filter; the pages are fixed when the job is queued | `builder:update` | Background job, dry run |
| Design | `edit_template` | Create a template as a copy, edit its sections and layout; the main section stays | `builder:update`, `builder:templates` | Dry run, version token |
| Design | `edit_template_rules` | Add, change, reorder, enable or disable rules; a rule points to an existing template only | `builder:update`, `builder:templates` | Dry run, version token |
| Design | `edit_theme_draft` | Header, footer, brand, and a palette generated from brand colors with a contrast check | `builder:update`, `builder:theme` | Dry run, version token |
| Design | `publish_theme` | Publish every theme draft; refused below WCAG 2.2 AA contrast | `builder:publish` | Approval bound to the reviewed drafts |
| Shared Components | `create_shared_component` | A new component, not placed anywhere yet | `builder:shared-components:create` | Dry run |
| Shared Components | `revise_shared_component` | New content or name; the content changes everywhere the component is placed | `builder:shared-components:update` | Approval when the content changes |

Bulk jobs are followed with the MCP module's `get_job_status`. Tool calls are audited by the MCP module, and **Agent tools** switches any of them off.

### Prompts and resources

MCP clients show the prompts as slash commands, for example `/mcp__virto__weekly_content_review` in Claude Code. Review prompts change nothing and ask before any action.

| Prompt | For | What it does |
| --- | --- | --- |
| `weekly_content_review` | Content author | What waits for publication, what changed this week, what is missing, with a short action list |
| `launch_landing_page` | Content author | From a brief to a landing page draft, its preview and its publication check |
| `translate_site` | Content author | Missing and outdated translations of a language, created and translated once you agree |
| `review_template_rules` | Site designer | Which template each kind of page gets, rules that never fire or point nowhere |
| `rebrand_site` | Site designer | Palette from a brand color, logos, header and footer, up to the theme's publication check |
| `review_theme_release` | Site designer | Every draft the theme's publication would take, who changed it, and what blocks it |
| `review_site_change` | Approver | What a requested change would do, who made it, and what to check before approving |

| Resource | What it holds | Permission |
| --- | --- | --- |
| `site-builder://stores/{store_id}/schemas` | Section, block, object and template types the storefront sent; not available until the storefront has sent its catalog | `builder:read` |
| `site-builder://stores/{store_id}/schemas/{kind}/{key}` | One schema with its settings | `builder:read` |
| `site-builder://template-rule-conditions` | The conditions a template rule can use, and the shape of their values | `builder:read` |
| `site-builder://stores/{store_id}/settings` | Languages, time zone and publication flow (git or blob) of a store | `builder:read`, `platform:setting:read` |
| `site-builder://user-groups` | User groups a page's audience can name, paged | `builder:read`, `platform:setting:read` |
| `site-builder://organizations` | Organizations a page's audience can name, by keyword, paged | `builder:read`, `customer:read` |

### What agents cannot do

These stay in the designer or with an administrator, on purpose:

- Delete or archive pages, or delete an empty Multilanguage Page: it cannot be undone.
- Delete Shared Components: it takes effect at once on every page.
- Promote content to the production branch of a store's repository (`builder:promote`).
- Upload, overwrite or delete assets and asset folders.
- Connect a store to GitHub, install the GitHub App, or clean up old drafts.
- Preview the storefront as another user: it needs `loginOnBehalf`.
- Check links to deleted products and categories, or section settings against the schemas: `find_content_gaps` says these are not checked, and why.
- Publish anything, or change what customers see, without a second person's approval.

## Shared Component creation

`POST /api/site-builder-shared-components` requires an existing store. An authorized request with an unknown `storeId` returns `400` naming that store, before any component, content, or asset references are written. Store-scoped authorization runs first; requests outside the user's store return `403`.

## Backup & Restore

Site Builder pages are included in the platform-wide backup and restore process.

When you run a platform export, all Site Builder pages (metadata and all variants including Draft, Published, and Archived) along with their JSON content are serialized into the backup archive.

On import, pages are recreated with new identifiers, preserving all metadata and content.

To run backup/restore:
1. Open Virto Commerce Admin UI.
1. Navigate to **Settings → Platform → Export** (or **Import**).
1. Ensure **Site Builder Module** is selected in the module list.
1. Run the export/import process.

## Concurrent page editing

Grouped-page content saves require the version the author originally read. `GET /api/site-builder-pages/grouped/{groupId}/content?draft=true` returns an `ETag`; send that exact value in `If-Match` when posting the edited document to the same URL. A successful save returns `204` and the next `ETag`. A stale version returns `412` without changing content or reference indexes. A missing version returns `428`; wildcard and weak tokens are rejected.

JSON clients can read `?draft=true&includeVersion=true` to receive `{ "content": "<page JSON>", "eTag": "<version>" }`, then post both fields to `/api/site-builder-pages/grouped/{groupId}/content-json`. An existing unseeded group has its own empty-state version: the raw GET returns `404` with an `ETag`, while the versioned GET returns a blank document and that token. A missing group has no token.

The Designer keeps local edits after a conflict and asks the author to copy them before reloading. Integrations must handle `412` the same way: read the new document and reapply the edit, rather than attach a fresh token to stale content. Update existing clients to send a version when upgrading the module. This contract covers grouped-page content editing; blob/theme content and Shared Component originals use their existing APIs.

### Upgrade notes

Grouped-page content POSTs now require a version. Deploy updated integrations together with this module. For proxies that transform HTTP ETag headers, use `includeVersion=true` on both GET and POST: GET returns `{ content, eTag }`, and a successful POST returns `200` with `{ eTag }`. The Designer uses these body tokens. Without that option, successful POSTs retain `204` plus the ETag header. An identical retry of an already accepted document succeeds with its current version, without a second persistence or index write.

`POST /api/site-builder-pages/create-group-page` requires a JSON object with a `content` array. The Shell import converts legacy top-level arrays to this object before submitting. Empty objects, legacy `{ pageContent: ... }` envelopes, null content, and sections without a type return `400`; empty or invalid JSON fails client parsing or server validation. Creation writes component and asset reference indexes in the same transaction as content. Add page also saves its initial settings and draft together.

The existing `IGroupedPageService` and `IContentStreamRepository` contracts remain unchanged. `IGroupedPageContentService` is registered separately in DI: the default registration uses a capable grouped service or the built-in authoring implementation, so existing grouped-service decorators keep working. Replacements that change authoring persistence should register this capability too. Customize Shell settings saves by overriding `SaveGroupSettingsAsync`; they no longer dispatch through a `SaveChangesAsync` override. Existing repository decorators can use the original queries, locks and asset-index method; `ISiteBuilderContentIndexRepository` is optional. Shell settings updates read the current document inside the same write lock; visibility-only updates preserve its bytes and version and do not create a draft when published content already exists.

Custom `builder_settings.json` descriptors replace the bundled descriptor as a whole. Theme overrides of grouped `templateUrl` and `saveGroupedPage` must retain `versioned: true` and `includeVersion=true` on their URLs. External unversioned backends keep their existing contract.

Closing the AI panel or receiving its `RELOAD_BLADE` message checks the current server version without replacing the local document's token. A changed, clean page reloads; a changed, dirty page keeps its edits and asks the author to reconcile. Unchanged pages produce no warning, and busy pages are left alone. Older assistants without save notifications are checked when their panel closes. Copy retains its newest Draft/Published source rule and rejects archived-only groups; publish and legacy direct content service calls retain their existing contracts.

## Getting started

### Prerequisites
1. Virto Commerce (`vc-platform`)  [Quick start](https://docs.virtocommerce.org/platform/developer-guide/)
1. Virto Frontend (`vc-frontend`).  [Deploy Frontend](https://docs.virtocommerce.org/storefront/developer-guide/deployment/)
1. Site Builder Module (`vc-module-site-builder`). [Download and Install](https://github.com/VirtoCommerce/vc-module-site-builder/releases).

### Setup Content
Check that Virto Commerce Platform and Storefront use same Shared Content folder.

### Setup Content Module
Content configuration should be extended with `PathMappings` section.

appsettings.json
```json
    "Content": {
        ...
        "PathMappings":{
            "pages": [
                "Themes",
                "_storeId",
                "_theme",
                "content/pages",
            ],
            "themes": [
                "Themes",
                "_storeId"
            ]
        }
        ...
    },
```

Environment Variables
```yaml
      Content__PathMappings__pages__0: "Themes"
      Content__PathMappings__pages__1: "_storeId"
      Content__PathMappings__pages__2: "_theme"
      Content__PathMappings__pages__3: "content/pages"
      Content__PathMappings__themes__0: "Themes"
      Content__PathMappings__themes__1: "_storeId"
```

### Setup Store
Public Store URL should be configured.

![image](https://user-images.githubusercontent.com/7639413/195312399-ec9a6c1e-e0d2-4798-8c37-1310f9c3a8ea.png)

1. Open Virto Commerce Admin UI.
1. Select `Stores`.
1. Select Current Store.
1. Setup Store URL if it's empty
1. Click Save button to apply.

### Purge Cache
You can purge static page from storefront cache by event. Otherwise, you will need to wait for cache expiration.

Virto Storefront has `/storefrontapi/content/reset-cache` end point for static page invalidation.

We recommend using Webhooks module.

1. Install `vc-module-webhooks` module.
1. Open Virto Commerce Admin UI.
1. Select Webhooks module.
1. Click `Add` button in toolbar to create a new webhook subscription.
1. Enter webhook subscription name.
1. Select `Site Builder Content Changed Event` event in Events drop-down.
1. Select `Path` and `Type` in additional fields.
1. Turn on `Activate` checkbox.
1. Enter Storefront end point in URL. Ex: https://www.mypublic-domain.com//storefrontapi/content/reset-cache
1. Save webhook subscription.

![image](docs/media/screen-webhook-settings.png)

### Run
1. Open Virto Commerce Admin UI.
1. Select Content module.
1. Find Store and Select Pages widget.
1. Click `Add` button in toolbar.
1. Select 'Design page'.
1. Provide page file name, public name and permalink. Ex: 'black-friday-2022', 'Black Friday 2022' and `blackfriday-2022`.
1. Click `Create` button

Now, you should be redirected to site builder application and you can create a first page.

Later, you can open and edit site builder pages, directly from content module.

## Site Builder Theme Structure
Site Builder reads meta-data from theme. Developers can extend or customize site builder behaviour by theme repository.

```text
├── client-app                    // The main folder for the application.
|   ├── shared                    // Assets needed to be precompiled during building.
|   |   └── static-content
|   |      └── components         // Vue Components for rendering Site Builder elements.
├── config
|   |   └── schemas               // All Site Builder meta information are stored here.
|   |      ├── blocks             // Block definitions.
|   |      |   └──...
|   |      ├── objects            // Object definitions.
|   |      |   └──...
|   |      ├── sections           // Sections definitions.
|   |      |   └──...
|   |      ├── shared             // Global settings folder.
|   |      |   └── _blocks.json   // Global settings for blocks.
|   |      |   └── _sections.json // Global settings for sections.
|   |      └── templates
|   |          └── page.json      // Site Builder configuration for static pages.
|   └── settings_schema.json      // Theme settings schema file. Site Builder uses it for Theme and Preset editor.
|   └── settings_data.json        // Theme config file.
```

## Notes

To enable the preview of your site within the Site Builder application, the site must allow itself to be displayed in an iframe. This can be achieved by setting the appropriate headers in your server configuration.

Add the following headers to your server configuration:

```http
Content-Security-Policy: "frame-ancestors 'self' https://localhost:5001;"
Cross-Origin-Resource-Policy: "cross-origin"
Cross-Origin-Embedder-Policy: "credentialless"
```

These headers ensure that the site can be embedded in an iframe and handle cross-origin requests appropriately.

## Documentation

* [PageBuidler module user documentation](https://docs.virtocommerce.org/platform/user-guide/page-builder/overview/)
* [AI Assistant for Site Builder (Virto OZ)](docs/ai-assistant.md)
* [REST API](https://virtostart-demo-admin.govirto.com/docs/index.html?urls.primaryName=VirtoCommerce.SiteBuilder)
* [View on GitHub](https://github.com/VirtoCommerce/vc-module-site-builder)

## References

* [Deployment](https://docs.virtocommerce.org/platform/developer-guide/Tutorials-and-How-tos/Tutorials/deploy-module-from-source-code/)
* [Installation](https://docs.virtocommerce.org/platform/user-guide/modules-installation/)
* [Home](https://virtocommerce.com)
* [Community](https://www.virtocommerce.org)
* [Download latest release](https://github.com/VirtoCommerce/vc-module-site-builder/releases/latest)

## License
Copyright (c) Virto Solutions LTD. All rights reserved.

Licensed under the Virto Commerce Open Software License (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at

http://virtocommerce.com/opensourcelicense

Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
