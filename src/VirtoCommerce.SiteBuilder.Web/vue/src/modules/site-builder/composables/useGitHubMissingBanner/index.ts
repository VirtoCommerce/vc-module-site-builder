import { onScopeDispose, toValue, watch, type MaybeRefOrGetter } from "vue";
import { useI18n } from "vue-i18n";
import { useApiClient, useBlade } from "@vc-shell/framework";
import { GitContentSetupClient } from "../../../../api_client/virtocommerce.sitebuilder";
import { gitHubMissing } from "../../../widgets/siteGit";

/**
 * Warns on the blade while the store keeps its pages in git and GitHub is not usable, which is why its pages are
 * refused. `open` shows the store's Git panel and calls back once it closes, to check again.
 */
export function useGitHubMissingBanner(storeId: MaybeRefOrGetter<string | undefined>, open: (closed: () => Promise<void>) => unknown) {
  const { t } = useI18n({ useScope: "global" });
  const { addBanner, removeBanner } = useBlade();
  const { getApiClient } = useApiClient(GitContentSetupClient);
  let banner: string | undefined;
  let disposed = false;

  async function isMissing(store: string) {
    const client = await getApiClient();
    // Without the live check only the settings are read; a store's status asks GitHub whenever GitHub is usable.
    const connection = await client.getConnection(false).catch(() => undefined);
    if (connection?.configured && !connection.error) return false;
    const status = await client.getSite(store).catch(() => undefined);
    return !!status && gitHubMissing(status);
  }

  async function refresh() {
    const store = toValue(storeId);
    const missing = !!store && (await isMissing(store));
    if (disposed || store !== toValue(storeId)) return;
    if (banner) removeBanner(banner);
    banner = missing
      ? addBanner({
          variant: "warning",
          message: t("SITE_BUILDER.GIT.GITHUB_MISSING"),
          action: { label: t("SITE_BUILDER.GIT.TITLE"), handler: () => open(refresh) },
        })
      : undefined;
  }

  watch(() => toValue(storeId), refresh, { immediate: true });
  onScopeDispose(() => {
    disposed = true;
    if (banner) removeBanner(banner);
  });

  return { refresh };
}
