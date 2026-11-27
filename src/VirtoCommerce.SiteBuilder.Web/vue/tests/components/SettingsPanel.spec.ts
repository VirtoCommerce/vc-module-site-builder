import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, effectScope, h, type PropType } from "vue";
import SettingsPanel from "../../src/modules/designer/components/SettingsPanel.vue";
import { useDesigner, type Designer } from "../../src/modules/designer/useDesigner";
import type { Item, Schemas } from "../../src/modules/designer/types";
import { fakePlatform, status } from "./support/fakePlatform";
import { notification } from "./support/vcShell";

// As VcDropdown does: a pick runs the item, and closes the menu only when asked to with closeOnSelect.
const VcDropdown = defineComponent({
  name: "VcDropdown",
  props: { modelValue: Boolean, items: { type: Array as PropType<{ title?: string }[]>, default: () => [] }, closeOnSelect: Boolean },
  emits: ["update:modelValue", "item-click"],
  setup(props, { emit, slots }) {
    const pick = (item: unknown) => {
      emit("item-click", item);
      if (props.closeOnSelect) emit("update:modelValue", false);
    };
    return () =>
      h("div", [
        slots.trigger?.({ isActive: props.modelValue, toggle: () => emit("update:modelValue", !props.modelValue) }),
        props.modelValue
          ? h("ul", { role: "menu" }, props.items.map((item) => h("li", { role: "menuitem", onClick: () => pick(item) }, slots.item?.({ item }) ?? item.title)))
          : null,
      ]);
  },
});

const SCHEMAS: Schemas = {
  sections: { promo: { name: "Promo", displayField: "title", settings: [{ id: "title", type: "string", translatable: true }] } },
  blocks: {},
  shared: {},
  objects: {},
};

let designer: Designer;
let scope: ReturnType<typeof effectScope>;

beforeEach(() => {
  scope = effectScope();
  designer = scope.run(() => useDesigner())!;
  designer.schemas.value = SCHEMAS;
  designer.languages.value = { all: ["en-US", "de-DE"], default: "en-US" };
  designer.language.value = "de-DE";
  designer.doc.value = { settings: { id: "settings", type: "settings" }, content: [{ id: "promo-1", type: "promo", title: { "de-DE": "Rabatt", "en-US": "Sale" } }] };
  designer.selection.value = { kind: "section", id: "promo-1" };
});

afterEach(() => scope.stop());

const mountPanel = () => mount(SettingsPanel, { props: { designer, selection: designer.selection.value! }, global: { stubs: { VcDropdown } } });
const menuItem = (wrapper: VueWrapper, text: string) => wrapper.findAll('[role="menuitem"]').find((x) => x.text() === text)!;

describe("SettingsPanel", () => {
  it("closes the actions menu once an action is picked", async () => {
    const wrapper = mountPanel();
    await wrapper.find('button[aria-label="More actions"]').trigger("click");

    await menuItem(wrapper, "Copy").trigger("click");

    expect(wrapper.emitted("copy")).toHaveLength(1);
    expect(wrapper.find('[role="menu"]').exists()).toBe(false);
  });

  it("titles a section by the default language's text, whichever language is edited", () => {
    expect(mountPanel().find(".pd-panel-head__title").text()).toBe("Sale");
  });

  it("tells the user, and leaves the placement in place, when the shared component's sections can't be read to detach it", async () => {
    fakePlatform().on("GET", "/api/site-builder-shared-components/footer/content", status(404)).install();
    designer.doc.value = { ...designer.doc.value, content: [{ id: "placed-1", type: "componentRef", componentRef: "footer" }] };
    designer.components.set("footer", { name: "Footer", usageCount: 2, content: [] });
    designer.selection.value = { kind: "section", id: "placed-1" };
    const wrapper = mountPanel();

    await wrapper.findAll("button").find((x) => x.text() === "Detach")!.trigger("click");
    await flushPromises();

    expect(notification.error).toHaveBeenCalledWith("The shared component couldn't be detached.");
    expect(designer.doc.value.content).toEqual([{ id: "placed-1", type: "componentRef", componentRef: "footer" }]);
  });

  describe("defaults added after the section", () => {
    const title = { id: "title", type: "string", label: "Title", translatable: true, default: { "en-US": "Feature", "de-DE": "Funktion" } };
    const label = { id: "label", type: "string", label: "Label", translatable: true, default: { "en-US": "More", "de-DE": "Mehr" } };

    beforeEach(() => {
      designer.schemas.value = {
        ...SCHEMAS,
        sections: {
          ...SCHEMAS.sections,
          features: { name: "Features", settings: [{ id: "items", type: "list", label: "Items", element: [title] }, { id: "cta", type: "object", label: "More", element: [label] }] },
        },
      };
    });

    const addRow = (wrapper: VueWrapper) => wrapper.findAll("button").find((x) => x.text() === "Add")!.trigger("click");
    const items = () => designer.doc.value.content[0].items;

    it("take the edited language's text on a page, which keeps one text, in a list row and in an object setting", async () => {
      designer.doc.value = { ...designer.doc.value, content: [{ id: "features-1", type: "features" }] };
      designer.selection.value = { kind: "section", id: "features-1" };
      const wrapper = mountPanel();

      await addRow(wrapper);

      expect(items()).toEqual([{ title: "Funktion" }]);
      expect((wrapper.find('input[aria-label="Label"]').element as HTMLInputElement).value).toBe("Mehr");
    });

    it("keep their texts by language in the header, which keeps every language", async () => {
      designer.doc.value = { ...designer.doc.value, content: [{ id: "features-1", type: "features", __group: "header" }] };
      designer.selection.value = { kind: "section", id: "features-1" };
      const wrapper = mountPanel();

      await addRow(wrapper);

      expect(items()).toEqual([{ title: { "en-US": "Feature", "de-DE": "Funktion" } }]);
    });
  });

  describe("a section's blocks", () => {
    beforeEach(() => {
      designer.schemas.value = {
        ...SCHEMAS,
        sections: { ...SCHEMAS.sections, bar: { name: "Announcement bar", blocks: ["announcement"] } },
        blocks: { announcement: { name: "Announcement", displayField: "text", settings: [{ id: "text", type: "string" }] } },
      };
    });

    const select = (content: Item[]) => {
      designer.doc.value = { ...designer.doc.value, content };
      designer.selection.value = { kind: "section", id: content[0].id };
    };

    it("are listed for a section that can hold them, and the one picked is opened", async () => {
      select([{ id: "bar-1", type: "bar", blocks: [{ id: "announcement-1", type: "announcement", text: "Free shipping" }] }]);
      const wrapper = mountPanel();

      expect(wrapper.findAll(".pd-settings__block").map((x) => x.text())).toEqual(["Free shipping"]);
      await wrapper.find(".pd-settings__block").trigger("click");

      expect(designer.selection.value).toEqual({ kind: "block", sectionId: "bar-1", id: "announcement-1" });
    });

    it("are said to be none yet, in place of nothing to set, and the type picked is added and opened", async () => {
      select([{ id: "bar-1", type: "bar", blocks: [] }]);
      const wrapper = mountPanel();
      expect(wrapper.find(".pd-settings__blocks").text()).toContain("No blocks yet.");
      expect(wrapper.text()).not.toContain("Nothing to set here.");

      await wrapper.findAll("button").find((x) => x.text() === "Add block")!.trigger("click");
      await menuItem(wrapper, "Announcement").trigger("click");

      const [block] = designer.doc.value.content[0].blocks!;
      expect(block).toMatchObject({ type: "announcement" });
      expect(designer.selection.value).toEqual({ kind: "block", sectionId: "bar-1", id: block.id });
      expect(wrapper.find('[role="menu"]').exists()).toBe(false);
    });

    it("are not shown for a section whose type holds none, nor for a shared component's placement", () => {
      expect(mountPanel().find(".pd-settings__blocks").exists()).toBe(false);

      designer.components.set("footer", { name: "Footer", usageCount: 2, content: [] });
      select([{ id: "placed-1", type: "componentRef", componentRef: "footer" }]);

      expect(mountPanel().find(".pd-settings__blocks").exists()).toBe(false);
    });
  });
});
