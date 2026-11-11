import { beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, ref } from "vue";
import { mount } from "@vue/test-utils";
import SiteBuilderPages from "../../src/modules/pages/SiteBuilderPages.vue";
import { BladeMaximizedKey, blade, resetVcShell } from "./support/vcShell";

vi.mock("../../src/modules/site-builder/components", () => ({ PagesList: defineComponent({ render: () => null }) }));
vi.mock("../../src/modules/site-builder/composables", async () => {
  const { ref: vueRef } = await import("vue");
  const count = () => vueRef<number | undefined>();
  return {
    PageLifecycleFilters: { Draft: "drafts", Active: "active", Archived: "archived", Pending: "pending" },
    pageCounts: { draft: count(), pending: count(), active: count(), archived: count(), all: count() },
    refreshMenuBadges: vi.fn(),
    useUrlParams: () => ({ setStoreId: vi.fn() }),
  };
});
vi.mock("../../src/modules/site-builder/composables/usePagesListToolbar", async () => {
  const { ref: vueRef } = await import("vue");
  return { usePagesListToolbar: () => vueRef([]) };
});

beforeEach(() => {
  resetVcShell();
  vi.stubGlobal("defineBlade", () => undefined);
});

describe("SiteBuilderPages", () => {
  it("opens maximized and maximizes again when it becomes the active blade", () => {
    const maximized = ref(false);
    mount(SiteBuilderPages, { global: { provide: { [BladeMaximizedKey]: maximized } } });
    expect(maximized.value).toBe(true);

    maximized.value = false;
    const [[activated]] = blade.onActivated.mock.calls;
    activated();
    expect(maximized.value).toBe(true);
  });
});
