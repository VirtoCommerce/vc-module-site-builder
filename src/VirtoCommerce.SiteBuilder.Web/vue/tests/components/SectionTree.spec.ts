import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { effectScope, nextTick } from "vue";
import SectionTree from "../../src/modules/designer/components/SectionTree.vue";
import { useDesigner, type Designer } from "../../src/modules/designer/useDesigner";
import type { Item, Schemas } from "../../src/modules/designer/types";

const SCHEMAS: Schemas = {
  sections: { promo: { name: "Promo", displayField: "title", settings: [{ id: "title", type: "string", translatable: true }], blocks: ["link"] } },
  blocks: { link: { name: "Link", displayField: "label", settings: [{ id: "label", type: "string", translatable: true }] } },
  shared: {},
  objects: {},
};

let designer: Designer;
let scope: ReturnType<typeof effectScope>;

beforeEach(() => {
  scope = effectScope();
  designer = scope.run(() => useDesigner())!;
  designer.schemas.value = SCHEMAS;
});

afterEach(() => scope.stop());

function mountTree(content: Item[]) {
  designer.doc.value = { settings: { id: "settings", type: "settings" }, content };
  return mount(SectionTree, { props: { designer } });
}

const button = (wrapper: VueWrapper, text: string) => wrapper.findAll("button").find((x) => x.text() === text);
const sectionNames = (wrapper: VueWrapper) => wrapper.findAll(".pd-tree__item .pd-tree__name").map((x) => x.text());

describe("SectionTree: select mode", () => {
  it("ends once every section is deleted, so Add section is back and the selection bar is gone", async () => {
    const wrapper = mountTree([{ id: "promo-1", type: "promo" }]);
    await button(wrapper, "Select")!.trigger("click");
    await wrapper.find("input.pd-tree__check").setValue(true);
    expect(button(wrapper, "Add section")).toBeUndefined();

    designer.removeChecked();
    await nextTick();

    expect(button(wrapper, "Add section")).toBeDefined();
    expect(wrapper.find(".pd-tree__bulk").exists()).toBe(false);
  });
});

describe("SectionTree: keyboard", () => {
  it("selects a row on Enter, but not on an Enter meant for a button inside the row", async () => {
    const wrapper = mountTree([{ id: "promo-1", type: "promo", blocks: [{ id: "link-1", type: "link" }] }]);
    await wrapper.find("button.pd-tree__toggle").trigger("click");

    for (const inner of wrapper.findAll("button.pd-tree__eye")) await inner.trigger("keydown", { key: "Enter" });
    await wrapper.find("button.pd-tree__toggle").trigger("keydown", { key: "Enter" });
    expect(designer.selection.value).toBeNull();

    await wrapper.find(".pd-tree__row--block").trigger("keydown", { key: "Enter" });
    expect(designer.selection.value).toEqual({ kind: "block", sectionId: "promo-1", id: "link-1" });
  });
});

describe("SectionTree: names", () => {
  it("names sections and blocks by the default language's text, whichever language is edited", async () => {
    designer.languages.value = { all: ["en-US", "de-DE"], default: "en-US" };
    designer.language.value = "de-DE";
    const wrapper = mountTree([
      { id: "promo-1", type: "promo", title: { "de-DE": "Rabatt", "en-US": "Sale" }, blocks: [{ id: "link-1", type: "link", label: { "de-DE": "Mehr", "en-US": "More" } }] },
    ]);
    await wrapper.find("button.pd-tree__toggle").trigger("click");

    expect(sectionNames(wrapper)).toEqual(["Sale", "More"]);
  });
});

describe("SectionTree: adding a section", () => {
  it("shows the designer's notice about the custom section types in the picker", async () => {
    designer.doc.value = { settings: { id: "settings", type: "settings" }, content: [] };
    const wrapper = mount(SectionTree, { props: { designer, customSectionsNotice: "Custom sections are shown as published." } });

    await button(wrapper, "Add section")!.trigger("click");

    expect(wrapper.find(".pd-add__notice").text()).toBe("Custom sections are shown as published.");
  });
});

describe("SectionTree: custom section types the storefront will not show", () => {
  it("marks a section whose custom type the theme lacks, and one whose type is not published yet, saying why", async () => {
    designer.schemas.value = { ...SCHEMAS, sections: { ...SCHEMAS.sections, "custom-numbers": { name: "Roastery in numbers", custom: true }, "custom-hero": { name: "Coffee hero", custom: true } } };
    designer.themeStatus.value = { publication: 3, drafts: ["custom-sections/custom-numbers.json"], newDrafts: ["custom-sections/custom-numbers.json"] };
    const wrapper = mountTree([
      { id: "promo-1", type: "promo" },
      { id: "hero-1", type: "custom-hero" },
      { id: "numbers-1", type: "custom-numbers" },
      { id: "gone-1", type: "custom-gone" },
      { id: "shared-1", type: "componentRef", componentRef: "custom-gone" },
    ]);
    await nextTick();

    const badges = wrapper.findAll(".pd-tree__item").map((row) => row.find(".pd-tree__badge--with-icon"));
    expect(badges.map((x) => x.exists())).toEqual([false, false, true, true, false]);
    const unpublished = 'Section type "Roastery in numbers" is not published yet: this section will not be shown until the theme is published.';
    const missing = 'Section type "custom-gone" not found: this section will not be shown on the storefront.';
    expect(badges[2].find(".pd-tree__badge-label").text()).toBe("Not published");
    expect(badges[2].attributes("title")).toBe(unpublished);
    expect(badges[2].find(".tw-sr-only").text()).toBe(unpublished);
    expect(badges[3].find(".pd-tree__badge-label").text()).toBe("Not found");
    expect(badges[3].attributes("title")).toBe(missing);
    expect(badges[3].find(".tw-sr-only").text()).toBe(missing);
  });
});
