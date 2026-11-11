import path from "node:path";
import { getDynamicModuleConfiguration } from "@vc-shell/mf-module";

// The platform serves the Admin Shell remote from this path.
const base = "/modules/$(VirtoCommerce.SiteBuilder)/plugins/admin-shell/";

export default getDynamicModuleConfiguration({
  appId: "admin-shell",
  moduleRoot: path.resolve(__dirname, ".."),
  entry: "./src/modules/index.ts",
  remoteName: "sitebuilder_admin",
  base,
});
