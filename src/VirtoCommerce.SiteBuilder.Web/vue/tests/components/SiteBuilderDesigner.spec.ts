import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, ref } from "vue";
import { flushPromises, mount, shallowMount, type VueWrapper } from "@vue/test-utils";
import { createMemoryHistory, createRouter } from "vue-router";
import type { IBladeToolbar } from "@vc-shell/framework";
import { VcBlade } from "@vc-shell/framework/ui";
import SiteBuilderDesigner from "../../src/modules/pages/SiteBuilderDesigner.vue";
import AssetsLibraryContent from "../../src/modules/asset-library/components/AssetsLibraryContent.vue";
import DiscardDialog from "../../src/modules/designer/components/DiscardDialog.vue";
import SectionTree from "../../src/modules/designer/components/SectionTree.vue";
import VersionHistory from "../../src/modules/designer/components/VersionHistory.vue";
import { pagesChanged, useUrlParams } from "../../src/modules/site-builder/composables";
import { PickAssetKey } from "../../src/modules/designer/types";
import { toClipboard } from "../../src/modules/designer/clipboard";
import type { Designer } from "../../src/modules/designer/useDesigner";
import { AiAgentServiceKey, BladeStackKey, blade, notification, popup } from "./support/vcShell";
import { fakePlatform, status, themeFileAnswer, type FakePlatform } from "./support/fakePlatform";

const STORE = "B2B-store";
const GROUP = "/api/site-builder-pages/grouped/g1";
const PAGE = { settings: { type: "settings" }, content: [{ id: "hero-1", type: "hero", title: "Welcome" }] };

beforeEach(() => {
  vi.stubGlobal("defineBlade", () => undefined);
  fakePlatform().install();
});

async function routerAt(address: string) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: "/:any(.*)*", component: { render: () => null } }] });
  await router.push(address);
  return router;
}

const toolbarItem = (wrapper: VueWrapper, id: string) =>
  (wrapper.findComponent(VcBlade).props("toolbarItems") as IBladeToolbar[]).find((x) => x.id === id)!;

describe("opening the designer", () => {
  it("says the page could not be loaded when its group cannot be read, and stops loading", async () => {
    blade.param.value = "g1";
    fakePlatform().on("GET", GROUP, status(403)).install();

    const wrapper = shallowMount(SiteBuilderDesigner, { global: { stubs: { VcBlade: false } } });
    await flushPromises();

    expect(wrapper.find(".pd-message").text()).toContain("couldn't be loaded");
    expect(wrapper.findComponent(VcBlade).props("loading")).toBe(false);
  });

  it("has nothing to save or discard when the page is gone", async () => {
    blade.param.value = "g1";
    fakePlatform().on("GET", GROUP, status(404)).install();

    const wrapper = shallowMount(SiteBuilderDesigner, { global: { stubs: { VcBlade: false } } });
    await flushPromises();

    expect(wrapper.find(".pd-message").exists()).toBe(true);
    expect(wrapper.findComponent(VcBlade).props("modified")).toBe(false);
    expect(toolbarItem(wrapper, "save").disabled).toBe(true);
    expect(toolbarItem(wrapper, "discard").isVisible).toBe(false);
  });
});

describe("reloading a designer opened by query", () => {
  it("opens the shared component the address names, and keeps it in the blade's query for the next reload", async () => {
    const platform = fakePlatform()
      .on("GET", "/api/site-builder-shared-components/c1", { id: "c1", storeId: STORE, name: "Promo", usageCount: 1 })
      .on("GET", "/api/site-builder-shared-components/c1/content", "[]")
      .install();
    const stack = { updateBladeQuery: vi.fn() };
    const router = await routerAt(`/site-builder-draft/site-builder-designer?storeId=${STORE}&sharedComponentId=c1&sort=name`);

    const wrapper = shallowMount(SiteBuilderDesigner, {
      global: { plugins: [router], provide: { [BladeStackKey]: stack }, stubs: { VcBlade: false } },
    });
    await flushPromises();

    expect(platform.to("/api/site-builder-shared-components/c1")).toHaveLength(1);
    expect(stack.updateBladeQuery).toHaveBeenCalledWith("blade", { storeId: STORE, sharedComponentId: "c1" });
    expect(wrapper.find(".pd").exists()).toBe(true);
  });

  it("reads a file page from the address the shell is still navigating to, as on Back", async () => {
    const stack = { updateBladeQuery: vi.fn() };
    const router = await routerAt("/site-builder-draft");
    // The browser's address has moved on, and the router has not finished its navigation there yet.
    router.options.history.push(`/site-builder-draft/site-builder-designer?storeId=${STORE}&path=about.page&type=pages&cultureName=de-DE&sort=name`);

    shallowMount(SiteBuilderDesigner, { global: { plugins: [router], provide: { [BladeStackKey]: stack }, stubs: { VcBlade: false } } });
    await flushPromises();

    expect(stack.updateBladeQuery).toHaveBeenCalledWith("blade", { storeId: STORE, path: "about.page", type: "pages", cultureName: "de-DE" });
  });

  it("says the page could not be loaded when the address names nothing to open", async () => {
    const router = await routerAt("/site-builder-draft/site-builder-designer");

    const wrapper = shallowMount(SiteBuilderDesigner, { global: { plugins: [router], stubs: { VcBlade: false } } });
    await flushPromises();

    expect(wrapper.find(".pd-message").text()).toContain("couldn't be loaded");
  });
});

describe("full view", () => {
  it("lets go of the window and gives the blade its header back when the page turns out not to be designable", async () => {
    localStorage.setItem("vc-page-designer-full-view", "true");
    blade.param.value = "g1";
    let readGroup!: (group: unknown) => void;
    fakePlatform()
      .on("GET", GROUP, () => new Promise((resolve) => (readGroup = resolve)))
      .install();

    const wrapper = shallowMount(SiteBuilderDesigner, { global: { stubs: { VcBlade: false } } });
    await flushPromises();
    const bladeElement = wrapper.find(".vc-blade");
    expect(bladeElement.classes()).toContain("pd-full-view");
    expect((bladeElement.element as HTMLElement).style.position).toBe("fixed");

    readGroup({ id: "g1", storeId: STORE, name: "Old", status: "Archived" });
    await flushPromises();

    expect(wrapper.find(".pd-message").exists()).toBe(true);
    expect(bladeElement.classes()).not.toContain("pd-full-view");
    expect((bladeElement.element as HTMLElement).style.position).toBe("");
    expect(toolbarItem(wrapper, "full-view").isVisible).toBe(false);
    expect(localStorage.getItem("vc-page-designer-full-view")).toBe("true");
  });
});

describe("the preview", () => {
  const NO_STOREFRONT = "the site has no storefront URL";

  function openPageOn(storefrontUrl: string | null) {
    fakePlatform()
      .on("GET", `/api/stores/${STORE}`, { id: STORE, dynamicProperties: [{ name: "DefaultThemeName", values: [{ value: "default" }] }] })
      .on("GET", GROUP, { id: "g1", storeId: STORE, name: "About", status: "Published" })
      .on("GET", `${GROUP}/content`, { content: JSON.stringify(PAGE), eTag: '"v1"' })
      .on("GET", "/api/site-builder-pages/grouped/publish-status/g1", { published: true, hasChanges: true })
      .on("GET", "/api/site-builder/designer/theme-file", themeFileAnswer({}))
      .on("GET", "/api/site-builder/theme/status", { publication: 3, drafts: [] })
      .on("GET", "/api/site-builder/designer/storefront", { url: storefrontUrl, previewPath: "/designer-preview" })
      .install();
    blade.param.value = "g1";
  }

  it("says the site has no storefront URL when it has none", async () => {
    openPageOn(null);
    const wrapper = shallowMount(SiteBuilderDesigner, { global: { plugins: [await routerAt("/")], stubs: { VcBlade: false } } });
    await flushPromises();

    expect(wrapper.find("iframe").exists()).toBe(false);
    expect(wrapper.text()).toContain(NO_STOREFRONT);
  });

  it("leaves the no-storefront message out once the storefront's preview has connected", async () => {
    openPageOn("https://shop.test");
    const wrapper = shallowMount(SiteBuilderDesigner, {
      attachTo: document.body,
      global: { plugins: [await routerAt("/")], stubs: { VcBlade: false } },
    });
    try {
      await flushPromises();
      const frame = wrapper.find("iframe").element as HTMLIFrameElement;
      const loaded = {
        protocol: 2,
        hash: "0badc0de",
        sections: {},
        blocks: {},
        shared: {},
        objects: {},
        paletteTokens: { light: [], dark: [] },
        capabilities: [],
        conditions: [],
      };

      const data = { source: "preview", type: "loaded", data: loaded };
      window.dispatchEvent(new MessageEvent("message", { source: frame.contentWindow, origin: "https://shop.test", data }));
      await flushPromises();

      expect(wrapper.find(".pd__frame-wait").exists()).toBe(false);
      expect(wrapper.text()).not.toContain(NO_STOREFRONT);
    } finally {
      wrapper.unmount();
    }
  });
});

describe("telling open lists the page changed", () => {
  let platform: FakePlatform;

  async function openPage() {
    platform = fakePlatform()
      .on("GET", `/api/stores/${STORE}`, { id: STORE, dynamicProperties: [{ name: "DefaultThemeName", values: [{ value: "default" }] }] })
      .on("GET", GROUP, { id: "g1", storeId: STORE, name: "About", status: "Published" })
      .on("GET", `${GROUP}/content`, { content: JSON.stringify(PAGE), eTag: '"v1"' })
      .on("GET", "/api/site-builder-pages/grouped/publish-status/g1", { published: true, hasChanges: true })
      .on("GET", "/api/site-builder/designer/theme-file", themeFileAnswer({}))
      .on("GET", "/api/site-builder/theme/status", { publication: 3, drafts: [] })
      .on("POST", "/api/site-builder/designer/discard", {})
      .on("GET", "/api/site-builder/history/status", { status: "enabled" })
      .on("POST", "/api/site-builder/history/v7/restore", "")
      .install();
    blade.param.value = "g1";
    const wrapper = shallowMount(SiteBuilderDesigner, { global: { plugins: [await routerAt("/")], stubs: { VcBlade: false } } });
    await flushPromises();
    return wrapper;
  }

  it("does so when the page's draft is discarded", async () => {
    const wrapper = await openPage();
    const before = pagesChanged.value;

    await wrapper.find('[role="toolbar"] [data-test-id="discard"]').trigger("click");
    await flushPromises();
    wrapper.findComponent(DiscardDialog).vm.$emit("discard", { page: true, theme: [] });
    await flushPromises();

    expect(platform.to("/api/site-builder/designer/discard", "POST")).toHaveLength(1);
    expect(pagesChanged.value).toBe(before + 1);
  });

  it("does so when a site version that holds the page is restored", async () => {
    const wrapper = await openPage();
    const before = pagesChanged.value;

    await wrapper.find('[role="toolbar"] [data-test-id="history"]').trigger("click");
    await flushPromises();
    wrapper.findComponent(VersionHistory).vm.$emit("restore", { id: "v7", label: "7", items: ["page:g1"] });
    await flushPromises();

    expect(platform.to("/api/site-builder/history/v7/restore", "POST")).toHaveLength(1);
    expect(pagesChanged.value).toBe(before + 1);
  });
});

describe("a page the AI assistant saves", () => {
  const CONTENT = `${GROUP}/content`;
  const STATUS = "/api/site-builder-pages/grouped/publish-status/g1";
  const SAVE = "/api/site-builder/designer/save";
  const ASSISTANT_PAGE = { settings: { type: "settings" }, content: [{ id: "hero-1", type: "hero", title: "Spring sale" }] };
  const NOTICE = "The AI assistant changed this page. Your unsaved edits are kept: copy what you need, then reopen the page to see its changes.";
  let platform: FakePlatform;
  let served: { content: string; eTag: string };
  let agent: ReturnType<typeof fakeAgent>;

  // As vc-shell's AI agent service: the panel's state, and the assistant's messages passed on to each listener.
  function fakeAgent() {
    const listeners = new Set<(message: { type: string }) => void>();
    return {
      isOpen: ref(true),
      onMessage: (listener: (message: { type: string }) => void) => {
        listeners.add(listener);
        return () => listeners.delete(listener);
      },
      send: (type: string) => listeners.forEach((listener) => listener({ type })),
    };
  }

  async function openPage() {
    served = { content: JSON.stringify(PAGE), eTag: '"v1"' };
    platform = fakePlatform()
      .on("GET", `/api/stores/${STORE}`, { id: STORE, dynamicProperties: [{ name: "DefaultThemeName", values: [{ value: "default" }] }] })
      .on("GET", GROUP, { id: "g1", storeId: STORE, name: "About", status: "Published" })
      .on("GET", CONTENT, () => served)
      .on("GET", STATUS, { published: true, hasChanges: true })
      .on("GET", "/api/site-builder/designer/theme-file", themeFileAnswer({}))
      .on("GET", "/api/site-builder/theme/status", { publication: 3, drafts: [] })
      .on("POST", SAVE, { eTag: '"v3"' })
      .install();
    blade.param.value = "g1";
    agent = fakeAgent();
    const wrapper = shallowMount(SiteBuilderDesigner, {
      global: { plugins: [await routerAt("/")], provide: { [AiAgentServiceKey]: agent }, stubs: { VcBlade: false } },
    });
    await flushPromises();
    return (wrapper.vm as unknown as { designer: Designer }).designer;
  }

  const assistantSaves = () => (served = { content: JSON.stringify(ASSISTANT_PAGE), eTag: '"v2"' });
  const heroTitle = (designer: Designer) => designer.doc.value.content.find((x) => x.id === "hero-1")?.title;
  const edit = (designer: Designer) =>
    (designer.doc.value = { ...designer.doc.value, content: designer.doc.value.content.map((x) => (x.id === "hero-1" ? { ...x, title: "Mine" } : x)) });
  const savedVersion = () => (platform.to(SAVE, "POST").at(-1)?.body as { page?: { eTag?: string } }).page?.eTag;

  it("reloads a page with no unsaved edits, with its version and publish status, once the assistant says it saved it", async () => {
    const designer = await openPage();
    const before = pagesChanged.value;
    assistantSaves();

    agent.send("RELOAD_BLADE");
    await flushPromises();

    expect(heroTitle(designer)).toBe("Spring sale");
    expect(designer.dirty.value).toBe(false);
    expect(platform.to(STATUS)).toHaveLength(2);
    expect(pagesChanged.value).toBe(before + 1);
    expect(notification.warning).not.toHaveBeenCalled();
    edit(designer);
    await designer.save();
    expect(savedVersion()).toBe('"v2"');
  });

  it("keeps unsaved edits and the version they started from, and says the assistant changed the page", async () => {
    const designer = await openPage();
    edit(designer);
    assistantSaves();

    agent.send("RELOAD_BLADE");
    await flushPromises();

    expect(heroTitle(designer)).toBe("Mine");
    expect(notification.warning).toHaveBeenCalledWith(NOTICE);
    expect(platform.to(STATUS)).toHaveLength(1);
    await designer.save();
    expect(savedVersion()).toBe('"v1"');
  });

  it("does nothing when the page is as it was", async () => {
    const designer = await openPage();

    agent.send("RELOAD_BLADE");
    await flushPromises();

    expect(platform.to(CONTENT)).toHaveLength(2);
    expect(platform.to(STATUS)).toHaveLength(1);
    expect(heroTitle(designer)).toBe("Welcome");
    expect(notification.warning).not.toHaveBeenCalled();
  });

  it("leaves a page alone while it is being saved", async () => {
    const designer = await openPage();
    assistantSaves();
    designer.busy.value = "saving";

    agent.send("RELOAD_BLADE");
    await flushPromises();

    expect(platform.to(CONTENT)).toHaveLength(1);
    expect(heroTitle(designer)).toBe("Welcome");
    expect(notification.warning).not.toHaveBeenCalled();
  });

  it("checks the page when the assistant's panel closes, for an assistant that does not say when it saved", async () => {
    const designer = await openPage();
    assistantSaves();

    agent.isOpen.value = false;
    await flushPromises();

    expect(heroTitle(designer)).toBe("Spring sale");
    expect(platform.to(STATUS)).toHaveLength(2);
  });
});

describe("the store the lists work on", () => {
  afterEach(() => history.replaceState(null, "", "/"));

  it("goes back to the store in the address, and the lists read again, once the designer of another store's page closes", async () => {
    history.replaceState(null, "", "/?storeId=Other-store");
    const storeContext = useUrlParams();
    storeContext.initUrlParams();
    fakePlatform()
      .on("GET", `/api/stores/${STORE}`, { id: STORE, dynamicProperties: [{ name: "DefaultThemeName", values: [{ value: "default" }] }] })
      .on("GET", GROUP, { id: "g1", storeId: STORE, name: "About", status: "Published" })
      .on("GET", `${GROUP}/content`, { content: JSON.stringify(PAGE), eTag: '"v1"' })
      .on("GET", "/api/site-builder-pages/grouped/publish-status/g1", { published: true, hasChanges: true })
      .on("GET", "/api/site-builder/designer/theme-file", themeFileAnswer({}))
      .on("GET", "/api/site-builder/theme/status", { publication: 3, drafts: [] })
      .install();
    blade.param.value = "g1";
    const wrapper = shallowMount(SiteBuilderDesigner, { global: { plugins: [await routerAt("/")], stubs: { VcBlade: false } } });
    await flushPromises();
    expect(storeContext.storeId.value).toBe(STORE);
    const before = pagesChanged.value;

    wrapper.unmount();

    expect(storeContext.storeId.value).toBe("Other-store");
    expect(pagesChanged.value).toBe(before + 1);
  });
});

describe("saving sections as a shared component", () => {
  it("creates one component when Save is clicked again while the first is on its way", async () => {
    let created!: (component: unknown) => void;
    const platform = fakePlatform()
      .on("GET", `/api/stores/${STORE}`, { id: STORE, dynamicProperties: [{ name: "DefaultThemeName", values: [{ value: "default" }] }] })
      .on("GET", GROUP, { id: "g1", storeId: STORE, name: "About", status: "Published" })
      .on("GET", `${GROUP}/content`, { content: JSON.stringify(PAGE), eTag: '"v1"' })
      .on("GET", "/api/site-builder-pages/grouped/publish-status/g1", { published: true, hasChanges: true })
      .on("GET", "/api/site-builder/designer/theme-file", themeFileAnswer({}))
      .on("GET", "/api/site-builder/theme/status", { publication: 3, drafts: [] })
      .on("POST", "/api/site-builder-shared-components", () => new Promise((resolve) => (created = resolve)))
      .install();
    blade.param.value = "g1";
    const wrapper = shallowMount(SiteBuilderDesigner, {
      global: { plugins: [await routerAt("/")], stubs: { VcBlade: false, VcPopup: false, VcInput: false, VcButton: false } },
    });
    await flushPromises();
    (wrapper.vm as unknown as { designer: { checked: { value: string[] } } }).designer.checked.value = ["hero-1"];
    wrapper.findComponent(SectionTree).vm.$emit("save-shared");
    await flushPromises();
    const dialog = wrapper.find('[role="dialog"]');
    await dialog.find("input").setValue("Promo");
    const save = dialog.findAll("button").find((x) => x.text() === "Save")!;

    await save.trigger("click");
    await save.trigger("click");
    created({ id: "c1", storeId: STORE, name: "Promo" });
    await flushPromises();

    expect(platform.to("/api/site-builder-shared-components", "POST")).toHaveLength(1);
  });
});

describe("picking an asset", () => {
  it("opens the asset library on the types the field asked for", async () => {
    blade.param.value = "g1";
    fakePlatform().on("GET", GROUP, status(403)).install();
    const wrapper = shallowMount(SiteBuilderDesigner, { global: { stubs: { VcBlade: false, VcPopup: false } } });
    await flushPromises();
    // Fields reach the picker only through this injection.
    const pickAsset = (wrapper.vm.$ as unknown as { provides: Record<symbol, (types?: string[]) => Promise<string | undefined>> }).provides[PickAssetKey as symbol];

    void pickAsset([".svg", "image/png"]);
    await flushPromises();

    expect(wrapper.findComponent(AssetsLibraryContent).props()).toMatchObject({ pickable: true, accept: [".svg", "image/png"] });
  });
});

describe("pasting from the clipboard", () => {
  afterEach(() => {
    delete (navigator as { clipboard?: unknown }).clipboard;
  });

  async function pasteWith(readText: () => Promise<string>) {
    Object.defineProperty(navigator, "clipboard", { value: { readText }, configurable: true });
    fakePlatform()
      .on("GET", `/api/stores/${STORE}`, { id: STORE, dynamicProperties: [{ name: "DefaultThemeName", values: [{ value: "default" }] }] })
      .on("GET", GROUP, { id: "g1", storeId: STORE, name: "About", status: "Published" })
      .on("GET", `${GROUP}/content`, { content: JSON.stringify(PAGE), eTag: '"v1"' })
      .on("GET", "/api/site-builder-pages/grouped/publish-status/g1", { published: true, hasChanges: true })
      .on("GET", "/api/site-builder/designer/theme-file", themeFileAnswer({}))
      .on("GET", "/api/site-builder/theme/status", { publication: 3, drafts: [] })
      .install();
    blade.param.value = "g1";
    const wrapper = shallowMount(SiteBuilderDesigner, {
      global: { plugins: [await routerAt("/")], stubs: { VcBlade: false, VcPopup: false, VcTextarea: false } },
    });
    await flushPromises();
    wrapper.findComponent(SectionTree).vm.$emit("paste");
    await flushPromises();
    return wrapper;
  }

  const pasteDialog = (wrapper: VueWrapper) => wrapper.find('[role="dialog"][aria-label="Paste a section or block"]');

  it("asks for the copied content when the browser does not let the page read the clipboard", async () => {
    const wrapper = await pasteWith(() => Promise.reject(new DOMException("denied", "NotAllowedError")));

    expect(pasteDialog(wrapper).exists()).toBe(true);
    expect(notification.error).not.toHaveBeenCalled();
  });

  it("takes a copied section far longer than 1024 characters in the paste dialog", async () => {
    const wrapper = await pasteWith(() => Promise.reject(new DOMException("denied", "NotAllowedError")));

    expect(Number(pasteDialog(wrapper).find("textarea").attributes("maxlength") ?? Infinity)).toBeGreaterThanOrEqual(100_000);
  });

  it.each([
    ["text that is not a copied item", "hello", "The clipboard doesn't hold a copied section or block."],
    ["a copied block with no section selected", toClipboard("block", { id: "b1", type: "button" }), "Select a section to paste a block into."],
  ])("says why it refuses %s, without asking for the clipboard again", async (_, text, message) => {
    const wrapper = await pasteWith(() => Promise.resolve(text));

    expect(notification.error).toHaveBeenCalledWith(message);
    expect(pasteDialog(wrapper).exists()).toBe(false);
  });
});

describe("the template switcher", () => {
  const SEARCH = `/api/content/themes/${STORE}/search`;
  // Renders the menu as well as the trigger, as an open VcDropdown does.
  const VcDropdown = defineComponent({
    name: "VcDropdown",
    setup: (_, { slots }) => () => h("div", [slots.trigger?.({ isActive: true, toggle: () => undefined }), slots["items-container"]?.({ close: () => undefined })]),
  });

  async function openWithTemplates(answer: unknown) {
    fakePlatform()
      .on("GET", `/api/stores/${STORE}`, { id: STORE, dynamicProperties: [{ name: "DefaultThemeName", values: [{ value: "default" }] }] })
      .on("GET", GROUP, { id: "g1", storeId: STORE, name: "About", status: "Published" })
      .on("GET", `${GROUP}/content`, { content: JSON.stringify(PAGE), eTag: '"v1"' })
      .on("GET", "/api/site-builder-pages/grouped/publish-status/g1", { published: true, hasChanges: true })
      .on("GET", "/api/site-builder/designer/theme-file", themeFileAnswer({}))
      .on("GET", "/api/site-builder/theme/status", { publication: 3, drafts: [] })
      .on("GET", SEARCH, answer)
      .install();
    blade.param.value = "g1";
    const wrapper = shallowMount(SiteBuilderDesigner, {
      global: { plugins: [await routerAt("/")], stubs: { VcBlade: false, TemplateSwitcher: false, VcDropdown } },
    });
    await flushPromises();
    return wrapper;
  }

  const items = (wrapper: VueWrapper) => wrapper.findAll('.pd-switch [role="menuitemradio"]').map((x) => x.text());

  it("lists the theme's alternate templates next to the default ones, with no notice", async () => {
    const wrapper = await openWithTemplates([{ name: "product.printers.json" }]);

    expect(items(wrapper)).toContain("Printers");
    expect(wrapper.find(".pd-switch__notice").exists()).toBe(false);
  });

  it.each([
    [403, "Only the default templates are shown: listing the theme's templates needs permission to read the site content."],
    [500, "The theme's templates couldn't be listed."],
  ])("says why it lists only the default templates when the theme's templates answer %i", async (code, message) => {
    const wrapper = await openWithTemplates(status(code));

    expect(wrapper.find(".pd-switch__notice").text()).toBe(message);
    expect(items(wrapper).filter((x) => x === "Default")).toHaveLength(3);
  });
});

describe("publishing a page at a permalink other pages have", () => {
  const PUBLISHING = "/api/site-builder-pages/grouped/publishing/g1";
  const USAGES = "/api/site-builder-pages/grouped/permalink-usages";
  const OLD = { groupId: "g2", name: "Old about", cultureName: "en-US", conflict: "Certain", canUnpublish: true };
  const ANY_LANGUAGE = { groupId: "g3", name: "About for all", conflict: "Certain", canUnpublish: true };
  let platform: FakePlatform;

  async function openPage(usages: unknown[], refusals: unknown[] = []) {
    platform = fakePlatform()
      .on("GET", `/api/stores/${STORE}`, { id: STORE, dynamicProperties: [{ name: "DefaultThemeName", values: [{ value: "default" }] }] })
      .on("GET", GROUP, { id: "g1", storeId: STORE, name: "About", permalink: "about", cultureName: "en-US", status: "Draft" })
      .on("GET", `${GROUP}/content`, { content: JSON.stringify(PAGE), eTag: '"v1"' })
      .on("GET", "/api/site-builder-pages/grouped/publish-status/g1", { published: false, hasChanges: true })
      .on("GET", "/api/site-builder/designer/theme-file", themeFileAnswer({}))
      .on("GET", "/api/site-builder/theme/status", { publication: 3, drafts: [] })
      .on("POST", USAGES, usages)
      .on("POST", PUBLISHING, () => {
        const conflicts = refusals.shift();
        return conflicts ? status(409, { error: "Pages are published with the same permalink", conflicts }) : "";
      })
      .install();
    blade.param.value = "g1";
    const wrapper = shallowMount(SiteBuilderDesigner, { global: { plugins: [await routerAt("/")], stubs: { VcBlade: false } } });
    await flushPromises();
    return wrapper;
  }

  async function publish(wrapper: VueWrapper) {
    await wrapper.find('[role="toolbar"] [data-test-id="publish"]').trigger("click");
    await flushPromises();
  }

  const publishes = () => platform.to(PUBLISHING, "POST").map((x) => x.query);
  const confirmations = () => popup.showConfirmation.mock.calls.map(([message]) => message);

  it("publishes at once when no other published page has the permalink", async () => {
    const wrapper = await openPage([{ groupId: "g4", name: "About draft", conflict: "None", canUnpublish: true }]);
    const before = pagesChanged.value;

    await publish(wrapper);

    expect(platform.to(USAGES, "POST")[0].body).toMatchObject({ id: "g1", permalink: "about", cultureName: "en-US" });
    expect(confirmations()).toEqual([]);
    expect(publishes()).toEqual([{ publish: "true" }]);
    expect(pagesChanged.value).toBe(before + 1);
  });

  it("asks to unpublish the pages it would replace, naming each with its language, and unpublishes them in the same publish", async () => {
    const wrapper = await openPage([OLD, ANY_LANGUAGE]);

    await publish(wrapper);

    expect(confirmations()).toEqual([
      'Already published at this permalink for the same visitors: "Old about" (en-US), "About for all". Unpublish them and publish this page?',
    ]);
    expect(publishes()).toEqual([{ publish: "true", unpublishConflicting: "true" }]);
  });

  it("asks before publishing over a page it meets for some visitors or dates only, and publishes nothing when declined", async () => {
    popup.confirm = false;
    const wrapper = await openPage([{ ...OLD, conflict: "Possible" }]);

    await publish(wrapper);

    expect(confirmations()).toEqual([
      'Also published at this permalink for some of the same visitors or dates: "Old about" (en-US). Where both pages apply, the one published last is shown. Publish anyway?',
    ]);
    expect(publishes()).toEqual([]);
  });

  it("offers to unpublish a page published after the check, and publishes again unpublishing it", async () => {
    const wrapper = await openPage([], [[OLD]]);
    const before = pagesChanged.value;

    await publish(wrapper);

    expect(confirmations()).toEqual([
      'Already published at this permalink for the same visitors: "Old about" (en-US). Unpublish them and publish this page?',
    ]);
    expect(publishes()).toEqual([{ publish: "true" }, { publish: "true", unpublishConflicting: "true" }]);
    expect(notification.error).not.toHaveBeenCalled();
    expect(pagesChanged.value).toBe(before + 1);
  });

  it("publishes nothing more and keeps Publish when the editor keeps the other page", async () => {
    popup.confirm = false;
    const wrapper = await openPage([], [[OLD]]);

    await publish(wrapper);

    expect(confirmations()).toHaveLength(1);
    expect(publishes()).toEqual([{ publish: "true" }]);
    expect(notification.error).not.toHaveBeenCalled();
    expect(toolbarItem(wrapper, "publish").disabled).toBe(false);
  });

  it("says the other page has changes to resolve first, and offers no unpublishing", async () => {
    const wrapper = await openPage([], [[{ ...OLD, canUnpublish: false }]]);

    await publish(wrapper);

    expect(confirmations()).toEqual([]);
    expect(publishes()).toEqual([{ publish: "true" }]);
    expect(notification.error).toHaveBeenCalledWith(
      'Already published at this permalink for the same visitors: "Old about" (en-US). They have unpublished changes, so publish or discard those changes first.',
    );
  });
});

describe("the theme's custom section types", () => {
  const SEARCH = `/api/content/themes/${STORE}/search`;
  const HERO = { format: 1, name: { "en-US": "Hero", "de-DE": "Held" }, schema: { settings: [{ id: "title", type: "string" }] }, template: "<section></section>", css: "" };
  const PROMO = { ...HERO, name: "Promo" };
  let platform: FakePlatform;

  async function openPage(drafts: string[]) {
    platform = fakePlatform()
      .on("GET", `/api/stores/${STORE}`, { id: STORE, dynamicProperties: [{ name: "DefaultThemeName", values: [{ value: "default" }] }] })
      .on("GET", GROUP, { id: "g1", storeId: STORE, name: "About", status: "Published" })
      .on("GET", `${GROUP}/content`, { content: JSON.stringify(PAGE), eTag: '"v1"' })
      .on("GET", "/api/site-builder-pages/grouped/publish-status/g1", { published: true, hasChanges: true })
      .on("GET", "/api/site-builder/designer/theme-file", themeFileAnswer({ "default/custom-sections/custom-hero.json": HERO, "default/custom-sections/custom-promo.json": PROMO }))
      .on("GET", "/api/site-builder/theme/status", { publication: 3, drafts })
      .on("GET", "/api/site-builder/designer/storefront", { url: "https://shop.test", previewPath: "/designer-preview" })
      .on("GET", "/api/site-builder/catalog", {})
      .on("GET", SEARCH, (request: { query: Record<string, string> }) =>
        request.query.folderUrl === "default/custom-sections" ? [{ name: "custom-hero.json-draft" }, { name: "custom-promo.json" }] : [],
      )
      .install();
    blade.param.value = "g1";
    const wrapper = shallowMount(SiteBuilderDesigner, { attachTo: document.body, global: { plugins: [await routerAt("/")], stubs: { VcBlade: false } } });
    await flushPromises();
    const frame = wrapper.find("iframe").element as HTMLIFrameElement;
    const posted = vi.spyOn(frame.contentWindow!, "postMessage");
    const load = async (hash: string, sections: Record<string, unknown> = {}, extra: Record<string, unknown> = {}) => {
      const data = { protocol: 2, hash, sections, blocks: {}, shared: {}, objects: {}, paletteTokens: { light: [], dark: [] }, capabilities: [], conditions: [], ...extra };
      window.dispatchEvent(new MessageEvent("message", { source: frame.contentWindow, origin: "https://shop.test", data: { source: "preview", type: "loaded", data } }));
      await flushPromises();
    };
    const lastDraft = () =>
      posted.mock.calls
        .map(([message]) => message as { type: string; theme?: Record<string, unknown> })
        .filter((x) => x.type === "draft")
        .at(-1);
    return { load, lastDraft, designer: (wrapper.vm as unknown as { designer: Designer }).designer };
  }

  it("go to the preview in the draft, each as last saved, once one of them has a draft", async () => {
    const page = await openPage(["custom-sections/custom-hero.json"]);

    await page.load("0badc0de");

    expect(page.lastDraft()?.theme?.customSections).toEqual({ "custom-hero": HERO, "custom-promo": PROMO });
  });

  it("replace the picker's types when the storefront sends loaded again with a new set, each named in the admin's language and listed once", async () => {
    const page = await openPage([]);
    // Hashes no other test loads: the designer remembers the catalogs the server has for as long as it runs.
    await page.load("c0ffee01", { text: { name: "Text" }, "custom-old": { name: "Old", custom: true, settings: [] } });
    const sections = { text: { name: "Text" }, "custom-hero": { name: { "en-US": "Hero", "de-DE": "Held" }, custom: true, settings: [] } };

    await page.load("c0ffee02", sections);
    await page.load("c0ffee02", sections);

    expect(page.designer.sectionTypes.value.map((x) => [x.type, x.schema.name])).toEqual([
      ["custom-hero", "Hero"],
      ["text", "Text"],
    ]);
    expect(platform.to("/api/site-builder/catalog").map((x) => x.query.hash)).toEqual(["c0ffee01", "c0ffee02"]);
  });

  it("leave the catalog the designer uploads as the storefront sent it, names by language and kit included", async () => {
    const page = await openPage([]);
    platform.on("GET", "/api/site-builder/catalog", status(404)).on("PUT", "/api/site-builder/catalog", "");
    const sections = { "custom-hero": { name: { "en-US": "Hero", "de-DE": "Held" }, custom: true, settings: [] } };
    const kit = { tags: { "vc-button": { attributes: { label: { type: "string" } } } }, behaviors: { inview: { options: {} } } };

    await page.load("c0ffee03", sections, { kit });

    const [upload] = platform.to("/api/site-builder/catalog", "PUT");
    expect(upload.body).toMatchObject({ hash: "c0ffee03", sections, kit });
    expect(page.designer.schemas.value.sections["custom-hero"].name).toBe("Hero");
  });

  it("are left to the published snapshot, and not read, while none has a draft", async () => {
    const page = await openPage(["templates/product.json"]);

    await page.load("0badc0de");

    expect(page.lastDraft()?.theme).not.toHaveProperty("customSections");
    expect(platform.to(SEARCH).filter((x) => x.query.folderUrl === "default/custom-sections")).toEqual([]);
  });
});

describe("the warnings a save answers with", () => {
  const WARNING = "custom-sections/custom-hero.json: the template's own text is the same in every language";

  async function saveAnswering(answer: unknown) {
    fakePlatform()
      .on("GET", `/api/stores/${STORE}`, { id: STORE, dynamicProperties: [{ name: "DefaultThemeName", values: [{ value: "default" }] }] })
      .on("GET", GROUP, { id: "g1", storeId: STORE, name: "About", status: "Published" })
      .on("GET", `${GROUP}/content`, { content: JSON.stringify(PAGE), eTag: '"v1"' })
      .on("GET", "/api/site-builder-pages/grouped/publish-status/g1", { published: true, hasChanges: true })
      .on("GET", "/api/site-builder/designer/theme-file", themeFileAnswer({}))
      .on("GET", "/api/site-builder/theme/status", { publication: 3, drafts: [] })
      .on("POST", "/api/site-builder/designer/save", answer)
      .install();
    blade.param.value = "g1";
    const wrapper = shallowMount(SiteBuilderDesigner, { global: { plugins: [await routerAt("/")], stubs: { VcBlade: false } } });
    await flushPromises();
    const designer = (wrapper.vm as unknown as { designer: Designer }).designer;
    designer.doc.value = { ...designer.doc.value, content: designer.doc.value.content.map((x) => ({ ...x, title: "Mine" })) };

    await toolbarItem(wrapper, "save").clickHandler!();
    await flushPromises();
    return designer;
  }

  it("lists them in a warning that stays until dismissed, with the save done", async () => {
    const designer = await saveAnswering({ eTag: '"v2"', warnings: [WARNING] });

    expect(notification.warning).toHaveBeenCalledTimes(1);
    const [content, options] = notification.warning.mock.calls[0];
    const toast = mount({ render: () => content });
    expect(toast.find("p").text()).toBe("Saved with warnings:");
    expect(toast.findAll("li").map((x) => x.text())).toEqual([WARNING]);
    expect(options).toEqual({ timeout: false });
    expect(notification.error).not.toHaveBeenCalled();
    expect(designer.dirty.value).toBe(false);
  });

  it("shows nothing for a save without warnings", async () => {
    await saveAnswering({ eTag: '"v2"', warnings: [] });

    expect(notification.warning).not.toHaveBeenCalled();
  });
});
