import { useBlade } from "@vc-shell/framework";
import { canOpenPageDesigner, type PageDesignerContext } from "../../../../utilities/pageDesigner";

export function usePageDesigner() {
  const { openBlade } = useBlade();

  return (context: PageDesignerContext, { onOpen, onClose }: { onOpen?: () => void; onClose?: () => void } = {}) => {
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
