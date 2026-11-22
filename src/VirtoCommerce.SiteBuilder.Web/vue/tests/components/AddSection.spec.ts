import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { effectScope } from "vue";
import AddSection from "../../src/modules/designer/components/AddSection.vue";
import { useDesigner, type Designer } from "../../src/modules/designer/useDesigner";
import type { ItemSchema, SectionGroup } from "../../src/modules/designer/types";
import { fakePlatform } from "./support/fakePlatform";

let designer: Designer;
let scope: ReturnType<typeof effectScope>;

beforeEach(() => {
  scope = effectScope();
  designer = scope.run(() => useDesigner())!;
});

afterEach(() => {
  scope.stop();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

async function pickerFor(sections: Record<string, ItemSchema>, group?: SectionGroup) {
  designer.schemas.value = { sections, blocks: {}, shared: {}, objects: {} };
  const wrapper = mount(AddSection, { props: { designer, group } });
  await flushPromises();
  return wrapper;
}

// Each heading with the names listed under it, in order.
const listed = (wrapper: VueWrapper) =>
  wrapper.findAll(".pd-add__group, .pd-add__item, .pd-add__empty").map((x) => (x.classes("pd-add__group") ? `# ${x.text()}` : x.text()));

describe("AddSection: the theme's custom section types", () => {
  it("lists them after the storefront's sections, under a heading of their own", async () => {
    const wrapper = await pickerFor({ text: { name: "Text" }, "custom-hero": { name: "Hero", custom: true }, banner: { name: "Banner" } });

    expect(listed(wrapper)).toEqual(["# Sections", "Banner", "Text", "# Custom sections", "Hero"]);
  });

  it("adds one like any other section", async () => {
    const wrapper = await pickerFor({ text: { name: "Text" }, "custom-hero": { name: "Hero", custom: true } });

    await wrapper.findAll(".pd-add__item").find((x) => x.text() === "Hero")!.trigger("click");

    expect(designer.doc.value.content.map((x) => x.type)).toEqual(["custom-hero"]);
  });

  it("shows only their heading when the search matches only them", async () => {
    const wrapper = await pickerFor({ text: { name: "Text" }, "custom-hero": { name: "Hero", custom: true } });

    await wrapper.find("input").setValue("her");

    expect(listed(wrapper)).toEqual(["# Custom sections", "Hero"]);
  });

  it("offers none in the header or footer, even one whose schema names the group", async () => {
    const sections = { "site-header": { name: "Header", groups: ["header" as const] }, "custom-hero": { name: "Hero", custom: true, groups: ["header" as const] } };

    expect(listed(await pickerFor(sections, "header"))).toEqual(["Header"]);
  });

  it("heads the storefront's sections while shared components are listed, with no custom types", async () => {
    vi.useFakeTimers();
    fakePlatform()
      .on("POST", "/api/site-builder-shared-components/search", { results: [{ id: "sc1", name: "Promo strip", usageCount: 2 }] })
      .install();
    designer.storeId.value = "s1";
    const wrapper = await pickerFor({ text: { name: "Text" }, banner: { name: "Banner" } });

    // the search waits for typing to pause
    await vi.advanceTimersByTimeAsync(300);
    await flushPromises();

    expect(listed(wrapper)).toEqual(["# Shared components", "Promo strip2 page(s)", "# Sections", "Banner", "Text"]);
  });

  it("lists the storefront's sections with no heading while the theme has no custom types", async () => {
    const wrapper = await pickerFor({ text: { name: "Text" }, banner: { name: "Banner" } });

    expect(listed(wrapper)).toEqual(["Banner", "Text"]);

    await wrapper.find("input").setValue("hero");

    expect(listed(wrapper)).toEqual(["No sections match"]);
  });
});
