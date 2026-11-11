# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

A **VirtoCommerce platform module** for site builder / theme editor functionality. The repo contains multiple sub-projects across different stacks:

| Sub-project | Stack | Location |
|---|---|---|
| C# backend | .NET 10, EF Core | `src/VirtoCommerce.SiteBuilder.*` |
| Angular 21 designer (stores with New Site Builder off) | Angular 21, NgRx, Signals | `src/.../Apps/site-builder-designer/` |
| Vue 3 UI (Admin Shell + standalone shell) | Vue 3, Vite, @vc-shell | `src/.../vue/` |
| AngularJS admin | AngularJS (legacy) | `src/.../Web/Scripts/` |

---

## Commands

### C# / .NET Backend

```bash
# Build solution
dotnet build VirtoCommerce.SiteBuilder.sln

# Run tests
dotnet test

# Build specific project
dotnet build src/VirtoCommerce.SiteBuilder.Web/
```

### Angular Designer

All commands run from: `src/VirtoCommerce.SiteBuilder.Web/Apps/site-builder-designer/`

```bash
npm start                    # Dev server
npm run build                # Full build (ngv-markdown → ngv-datepicker → themes → app)
npm test                     # Tests
npm run lint                 # ESLint
```

### Vue 3 Shell

All commands run from: `src/VirtoCommerce.SiteBuilder.Web/vue/` (one project for the Admin Shell UI and the standalone app)

```bash
yarn build                   # Admin Shell UI (Module Federation remote) -> plugins/admin-shell/
yarn build:standalone        # Standalone app -> Apps/site-builder-shell/dist (vite.standalone.config.mts)
yarn type-check              # Vue-tsc type checking (both)
yarn test                    # node:test unit tests (tests/)
```

`Apps/site-builder-shell/` keeps only a package.json whose `build` runs `build:standalone` here, for vc-build's app build.

---

## Architecture

### C# Module Structure

Standard VirtoCommerce module pattern:

- **`Core/`** — Domain models, service interfaces, events, `ModuleConstants`. No dependencies on Data or Web.
- **`Data/`** — EF Core DbContext, entity models, service implementations, authorization handlers, search indexing. Depends on Core and platform.
- **`Data.SqlServer/`, `Data.MySql/`, `Data.PostgreSql/`** — Database provider projects. Each contains EF Core migrations for its target DB.
- **`Xapi/`** — Storefront GraphQL: the `themeSnapshot` query, and the x-cms `builderPage` query answered from Site Builder pages (its handler and `BuilderPageItem` type replace x-cms's, which read Page Builder). Depends on Core.
- **`Web/`** — .NET Web SDK project (output is a library, not executable). Contains API controllers, AngularJS scripts, and serves the compiled Vue apps. Depends on Core + Data.
- **`Tests/`** — xUnit v3 tests.

**Version:** 3.1001.0 (defined in `Directory.Build.props`). Platform dependency: VirtoCommerce 3.1000.0+.

**Key C# patterns:**
- `TreatWarningsAsErrors: true` — no suppressed warnings
- Event-driven architecture: `SiteBuilderPageChanging/Changed`, `GroupedPageChanging/Changed`
- Content streaming via `ContentStreamRepository` / `IContentStreamProvider`
- Authorization via `SiteBuilderAuthorizationHandler` + `SiteBuilderAuthorizationRequirement`

### Angular 20 Designer App

Lives entirely in `Apps/site-builder-designer/`. The SPA runs as an `<iframe>` inside the VirtoCommerce platform admin shell.

**Module structure** (`src/app/modules/`):
- **`core/`** — Services (HTTP, assets, clipboard, modal, notifications, event bus), UI controls, dialogs. Eagerly provided.
- **`editor/`** — Page/template editing. Lazy-loaded at `/pages`. Has its own NgRx store (three-layer: data/domain/ui).
- **`theme/`** — Theme editor. Lazy-loaded at `/themes`. Has its own NgRx store.
- **`shared/`** — Components, dialogs, services, NgRx routing state. Shared across features.
- **`integration/`** — Platform `postMessage`/`BroadcastChannel` integration. `AppInitializator` runs at startup via `APP_INITIALIZER`.
- **`models/`** — Pure TypeScript model definitions.

**Platform integration:**
- Auth JWT read from `localStorage` (`ls.authenticationData`) — set by VC platform shell
- Communicates with parent via `postMessage` and `BroadcastChannel` (`vc-module-content-channel`)
- Preview iframe receives template data changes in real-time

**Config system:**
- Loads `data/settings.json` (or another bundled `data/*.json` named by `?configUrl=`)
- Template syntax in values (evaluated by `appHelpers.template()` in `integration/helpers/utils.ts`):
  - `{{path}}` — substitutes value by dot-path from context (`location.*`, `config.*`, `settings.*`)
  - `{{=expr}}` — evaluates an expression, result is coerced to string (for use inside larger strings)
  - `@{{expr}}` — evaluates an expression, returns raw result preserving type (array, object, number). Must be the entire string value. Use for POST body fields that need non-string types.
  - Expressions are interpreted by `integration/helpers/safe-expression.ts`, not run as JS: no functions or callbacks, object literals, `new` or globals (see `docs/_template-syntax.md`, "Expression language"). Anything else evaluates to `null`.
- `BuilderHttpClient` evaluates `ServerRequestDescriptor` objects with LRU cache + fallback chains

**Internal libraries (`projects/`):**
- **`ngv-datepicker`** — Custom datepicker with clock view. Has prebuilt Sass themes (compiled to `.dist-libs/`).
- **`ngv-markdown`** — Markdown editor wrapping EasyMDE + Turndown (HTML→Markdown paste).

### Vue 3 Shell App

Lives in `vue/`: shared code in `vue/src/` (api_client, utilities, locales, the site-builder / asset-library / shared-components modules), the Admin Shell entry in `vue/src/modules/index.ts` (blades SiteBuilderPages, SiteBuilderPageDetails, SiteBuilderDesigner, SiteBuilderAssets, SiteBuilderSharedComponents; the StoreDetails widget and the Sites "Pages" tab), the standalone app (served at `/apps/site-builder-shell/`) in `vue/standalone/`. Built with Vite 6 and uses the `@vc-shell/framework` for VirtoCommerce module shell integration. Uses Vue 3 Composition API, `vue-router`, `vee-validate` for forms, and auto-generated API client from `@vc-shell/api-client-generator`.

The page designer is the full-screen `SiteBuilderDesigner` blade (`vue/src/modules/pages/SiteBuilderDesigner.vue`); its state, schema, document and preview code live in `vue/src/modules/designer/`. It edits database pages (param = page group id) and pages kept as files (query: storeId, path, type), with the git version history when the store keeps its pages in git. The preview is the storefront's `/designer-preview` page in an iframe, driven over postMessage (`designer/preview.ts`). The standalone app opens it from `/apps/site-builder-shell/?storeId=…&designGroup=…` or `&designPath=…&designType=…`, which is what the classic admin's page blade links to.

### AngularJS Admin Integration

Located at `Web/Scripts/`. Module registered as `virtoCommerce.siteBuilderModule`. Provides:
- Page editing blade (`blades/pages/edit-page.js`)
- Store detail widget (`widgets/siteBuilder-app-widget.js`)
- API resource (`resources/siteBuilderApi.js`)
- File upload handler (`services/site-builder-file.handler.js`)

This is **legacy code** — do not add new features here. It integrates with the VirtoCommerce admin blade architecture.

---

## CI/CD

- `.github/workflows/module-ci.yml` — main pipeline (Node 20, build + package NuGet)
- `.github/workflows/deploy.yml` — deployment
- `.github/workflows/publish-nugets.yml` — NuGet publishing
- `Jenkinsfile` — Jenkins pipeline
- Build output: `dist/template-builder/` — deployed as part of the .NET module
