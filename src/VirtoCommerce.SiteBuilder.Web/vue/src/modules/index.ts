import locales from "./locales";
import { defineAppModule, registerExternalWidget } from "@vc-shell/framework";
import { markRaw } from "vue";
import SitePagesTab from "./widgets/SitePagesTab.vue";
import { SiteAssetsWidget, SiteGitWidget, SiteSharedComponentsWidget, SitesGitHubWidget, StoreSiteBuilderWidget } from "./widgets/siteWidgets";
import SiteBuilderPages from "./pages/SiteBuilderPages.vue";
import SiteBuilderAssets from "./pages/SiteBuilderAssets.vue";
import SiteBuilderSharedComponents from "./pages/SiteBuilderSharedComponents.vue";
import SiteBuilderDesigner from "./pages/SiteBuilderDesigner.vue";
import SiteBuilderGit from "./pages/SiteBuilderGit.vue";
import SiteBuilderGitHub from "./pages/SiteBuilderGitHub.vue";
import SiteBuilderPageDetails from "./site-builder/pages/SiteBuilderPageDetails.vue";
import MultilanguagePageDetails from "./site-builder/pages/MultilanguagePageDetails.vue";
import { definePermalinkRule } from "./site-builder/permalinkRule";
import "./styles.css";

definePermalinkRule();

registerExternalWidget({
  id: "SiteBuilderStore",
  title: "Site Builder",
  component: StoreSiteBuilderWidget,
  targetBlades: ["StoreDetails"],
  isVisible: (blade) => !!blade?.param,
});

registerExternalWidget({
  id: "site-content:pages",
  title: "SITE_BUILDER.SITE_TAB.TITLE",
  component: markRaw(SitePagesTab),
  targetBlades: ["SiteDetails:content"],
});
registerExternalWidget({ id: "SiteAssets", title: "Assets Library", component: SiteAssetsWidget, targetBlades: ["SiteDetails"] });
registerExternalWidget({
  id: "SiteSharedComponents",
  title: "Shared Components",
  component: SiteSharedComponentsWidget,
  targetBlades: ["SiteDetails"],
});

registerExternalWidget({ id: "SiteGit", title: "History", component: SiteGitWidget, targetBlades: ["SiteDetails"] });
registerExternalWidget({ id: "SitesGitHub", title: "History", component: SitesGitHubWidget, targetBlades: ["ContentStores"] });

export default defineAppModule({
  blades: {
    SiteBuilderPages,
    SiteBuilderPageDetails,
    MultilanguagePageDetails,
    SiteBuilderDesigner,
    SiteBuilderAssets,
    SiteBuilderSharedComponents,
    SiteBuilderGit,
    SiteBuilderGitHub,
  },
  locales,
});
