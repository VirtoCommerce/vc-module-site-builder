import { beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";
import { mount } from "@vue/test-utils";
import { usePageDesigner } from "../../src/modules/site-builder/composables/usePageDesigner";

const shell = vi.hoisted(() => ({ openBlade: vi.fn() }));

vi.mock("@vc-shell/framework", async (importOriginal) => {
  const original = await importOriginal<typeof import("./support/vcShell")>();
  return {
    ...original,
    useBlade: () => ({ ...original.useBlade(), openBlade: shell.openBlade }),
  };
});

const CONTEXT = { groupId: "g1", storeId: "B2B-store" };

function pageDesigner() {
  let open!: ReturnType<typeof usePageDesigner>;
  mount(
    defineComponent({
      setup() {
        open = usePageDesigner();
        return () => null;
      },
    }),
  );
  return open;
}

describe("opening the page designer", () => {
  beforeEach(() => {
    shell.openBlade.mockClear();
  });

  it("opens the designer blade, with the caller's open and close callbacks", () => {
    const onOpen = vi.fn();
    const onClose = vi.fn();

    pageDesigner()(CONTEXT, { onOpen, onClose });

    expect(shell.openBlade).toHaveBeenCalledWith({ name: "SiteBuilderDesigner", param: "g1", onOpen, onClose });
  });
});
