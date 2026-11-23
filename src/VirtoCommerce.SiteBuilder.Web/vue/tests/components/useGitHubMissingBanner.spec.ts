import { beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, ref } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { useGitHubMissingBanner } from "../../src/modules/site-builder/composables/useGitHubMissingBanner";
import { blade } from "./support/vcShell";
import { fakePlatform, type FakePlatform } from "./support/fakePlatform";

const SITE = "/api/site-builder/git/site";
const CONNECTION = "/api/site-builder/git/connection";

const MESSAGE = "This store publishes pages through Git, but GitHub is not connected. Connect GitHub, or turn off Git publishing for this store.";

let platform: FakePlatform;
const open = vi.fn();
const storeId = ref<string | undefined>("s1");

function mountHost() {
  return mount(
    defineComponent({
      setup() {
        useGitHubMissingBanner(storeId, open);
        return () => null;
      },
    }),
  );
}

beforeEach(() => {
  platform = fakePlatform().install();
  open.mockReset();
  storeId.value = "s1";
});

describe("the warning that a git store has no GitHub", () => {
  it("shows while the store keeps its pages in git and GitHub is not usable", async () => {
    platform.on("GET", CONNECTION, { configured: false }).on("GET", SITE, { configured: false, enabled: true });

    mountHost();
    await flushPromises();

    expect(blade.banners).toEqual([expect.objectContaining({ variant: "warning", message: MESSAGE, action: expect.objectContaining({ label: "Version history" }) })]);
    expect(platform.to(SITE)[0].query).toEqual({ storeId: "s1" });
  });

  it("shows for a connection whose saved App cannot be read", async () => {
    platform
      .on("GET", CONNECTION, { configured: false, error: "The saved GitHub connection could not be read." })
      .on("GET", SITE, { configured: false, enabled: true, error: "The saved GitHub connection could not be read." });

    mountHost();
    await flushPromises();

    expect(blade.banners).toHaveLength(1);
  });

  it("stays hidden while GitHub is usable, without asking for the store's status", async () => {
    platform.on("GET", CONNECTION, { configured: true }).on("GET", SITE, { configured: true, enabled: true });

    mountHost();
    await flushPromises();

    expect(blade.banners).toEqual([]);
    expect(platform.to(SITE)).toEqual([]);
    expect(platform.to(CONNECTION)[0].query).toEqual({ live: "false" });
  });

  it.each([
    ["a store that keeps its pages on the platform", { configured: false, enabled: false }],
    ["git switched off for the whole platform", { configured: false }],
  ])("stays hidden for %s", async (_case, site) => {
    platform.on("GET", CONNECTION, { configured: false }).on("GET", SITE, site);

    mountHost();
    await flushPromises();

    expect(blade.banners).toEqual([]);
  });

  it("stays hidden when the status cannot be read", async () => {
    platform.on("GET", CONNECTION, { configured: false });

    mountHost();
    await flushPromises();

    expect(blade.banners).toEqual([]);
  });

  it("opens the Git panel and checks again once it closes", async () => {
    platform.on("GET", CONNECTION, { configured: false }).on("GET", SITE, { configured: false, enabled: true });
    mountHost();
    await flushPromises();

    blade.banners[0].action?.handler();
    expect(open).toHaveBeenCalledOnce();
    platform.on("GET", SITE, { configured: false, enabled: false });
    await open.mock.calls[0][0]();

    expect(blade.banners).toEqual([]);
  });

  it("checks the store it is moved to, and goes with its blade", async () => {
    platform.on("GET", CONNECTION, { configured: false }).on("GET", SITE, (request) => ({ configured: false, enabled: request.query.storeId === "s2" }));
    const wrapper = mountHost();
    await flushPromises();
    expect(blade.banners).toEqual([]);

    storeId.value = "s2";
    await flushPromises();
    expect(blade.banners).toHaveLength(1);

    wrapper.unmount();
    expect(blade.banners).toEqual([]);
  });
});
