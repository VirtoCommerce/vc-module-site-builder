import { notification, useBlade } from "@vc-shell/framework";
import { ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { canOpenPageDesigner, openPageDesigner, type PageDesignerContext } from "../../../../utilities/pageDesigner";
import useUrlParams from "../useStoreParams";

export function usePageDesigner() {
  const { t } = useI18n({ useScope: "global" });
  const { openBlade } = useBlade();
  const { storeId, isNewSiteBuilder } = useUrlParams();

  // Read before the click: the browser blocks a tab opened after the platform answers as a pop-up.
  const known = ref<{ storeId: string; newSiteBuilder: boolean }>();
  watch(
    storeId,
    async (id) => {
      known.value = undefined;
      const newSiteBuilder = id ? await isNewSiteBuilder(id) : undefined;
      if (id && id === storeId.value && newSiteBuilder !== undefined) known.value = { storeId: id, newSiteBuilder };
    },
    { immediate: true },
  );

  // A designer in another tab never reports back, so the callbacks run only for the designer blade.
  return async (context: PageDesignerContext, { onOpen, onClose }: { onOpen?: () => void; onClose?: () => void } = {}) => {
    const ahead = known.value;
    const newSiteBuilder = ahead && ahead.storeId === context.storeId ? ahead.newSiteBuilder : await isNewSiteBuilder(context.storeId);
    if (newSiteBuilder === undefined) {
      notification.error(t("SITE_BUILDER.DESIGNER.STORE_READ_FAILED"));
      return;
    }
    if (!newSiteBuilder) {
      openPageDesigner(context);
      return;
    }
    if (!canOpenPageDesigner(context)) {
      throw new Error("Can't open page.");
    }
    openBlade({ name: "SiteBuilderDesigner", param: context.groupId, onOpen, onClose });
  };
}

export function useSharedComponentDesigner() {
  const { openBlade } = useBlade();

  return (storeId: string, sharedComponentId: string, onClose?: () => void) =>
    openBlade({ name: "SiteBuilderDesigner", query: { storeId, sharedComponentId }, onClose });
}
