import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent } from "vue";
import { CODE_LIFETIME_MS, FAILED_RENEWAL_DELAYS_MS, usePreviewUser } from "../../src/modules/designer/usePreviewUser";
import type { AuthMessage } from "../../src/modules/designer/previewMessages";
import { fakePlatform, status, type FakePlatform } from "./support/fakePlatform";

const STORE = "B2B-store";
const CODE = "/api/site-builder/preview-auth/code";

let platform: FakePlatform;
let codes: number;

beforeEach(() => {
  vi.useFakeTimers();
  localStorage.clear();
  codes = 0;
  platform = fakePlatform()
    .on("GET", `/api/stores/${STORE}`, { settings: [{ name: "SiteBuilder.Store.PreviewUserIds", value: "u1\nu2" }] })
    .on("POST", "/api/platform/security/users/search", { results: [{ id: "u1", userName: "anna" }, { id: "u2", userName: "ben" }] })
    .on("POST", CODE, () => ({ code: `c${++codes}`, expiresInSeconds: 60 }))
    .install();
});

afterEach(() => {
  vi.useRealTimers();
});

async function previewUser() {
  const sent: AuthMessage[] = [];
  const onFailed = vi.fn();
  let previewAs!: ReturnType<typeof usePreviewUser>;
  const wrapper = mount(
    defineComponent({
      setup() {
        previewAs = usePreviewUser({ send: (message) => sent.push(message), onFailed });
        return () => null;
      },
    }),
  );
  await previewAs.load(STORE);
  return { previewAs, sent, onFailed, wrapper };
}

describe("previewing as a store's preview user", () => {
  it("sends a new code with every auth for the user, asked for the store and the user", async () => {
    const { previewAs, sent } = await previewUser();

    await previewAs.pick("u1");
    expect(sent).toEqual([{ type: "auth", code: "c1", userId: "u1" }]);
    expect(platform.to(CODE)[0].body).toEqual({ storeId: STORE, userId: "u1" });

    // a reloaded frame's handshake takes a code of its own
    await previewAs.refresh();
    expect(previewAs.message()).toEqual({ type: "auth", code: "c2", userId: "u1" });

    await previewAs.pick(null);
    expect(sent.at(-1)).toEqual({ type: "auth", token: null, userId: null });
    expect(platform.to(CODE)).toHaveLength(2);
  });

  it("answers authRenew before the token expires at once", async () => {
    const { previewAs, sent } = await previewUser();
    await previewAs.pick("u1");

    await vi.advanceTimersByTimeAsync(CODE_LIFETIME_MS * 20);
    previewAs.renew("u1");
    previewAs.renew("u1");
    await vi.advanceTimersByTimeAsync(0);
    await flushPromises();

    expect(platform.to(CODE)).toHaveLength(2);
    expect(sent.at(-1)).toEqual({ type: "auth", code: "c2", userId: "u1" });
  });

  it("answers authRenew after failed exchanges ever later, then shows the anonymous visitor", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    const { previewAs, sent, onFailed } = await previewUser();
    await previewAs.pick("u1");

    for (const [index, delay] of FAILED_RENEWAL_DELAYS_MS.entries()) {
      previewAs.renew("u1");
      await vi.advanceTimersByTimeAsync(delay - 1);
      expect(platform.to(CODE)).toHaveLength(index + 1);
      await vi.advanceTimersByTimeAsync(1);
      await flushPromises();
      expect(sent.at(-1)).toEqual({ type: "auth", code: `c${index + 2}`, userId: "u1" });
    }

    previewAs.renew("u1");
    await vi.advanceTimersByTimeAsync(CODE_LIFETIME_MS);
    await flushPromises();

    expect(platform.to(CODE)).toHaveLength(FAILED_RENEWAL_DELAYS_MS.length + 1);
    expect(sent.at(-1)).toEqual({ type: "auth", token: null, userId: null });
    expect(previewAs.userId.value).toBeNull();
    expect(onFailed).toHaveBeenCalledWith({ id: "u1", name: "anna" });
    expect(warn).toHaveBeenCalledOnce();
    warn.mockRestore();
  });

  it("starts the failed renewals over once a token was renewed near its expiry", async () => {
    const { previewAs, sent, onFailed } = await previewUser();
    await previewAs.pick("u1");

    for (const delay of FAILED_RENEWAL_DELAYS_MS.slice(0, -1)) {
      previewAs.renew("u1");
      await vi.advanceTimersByTimeAsync(delay);
      await flushPromises();
    }
    await vi.advanceTimersByTimeAsync(CODE_LIFETIME_MS * 20);
    previewAs.renew("u1");
    await vi.advanceTimersByTimeAsync(0);
    await flushPromises();

    previewAs.renew("u1");
    await vi.advanceTimersByTimeAsync(FAILED_RENEWAL_DELAYS_MS[0]);
    await flushPromises();

    expect(onFailed).not.toHaveBeenCalled();
    expect(sent.at(-1)).toEqual({ type: "auth", code: `c${FAILED_RENEWAL_DELAYS_MS.length + 2}`, userId: "u1" });
  });

  it("leaves authRenew for a user no longer picked unanswered", async () => {
    const { previewAs, sent } = await previewUser();
    await previewAs.pick("u1");
    await previewAs.pick("u2");

    previewAs.renew("u1");
    await vi.advanceTimersByTimeAsync(CODE_LIFETIME_MS);
    await flushPromises();

    expect(platform.to(CODE).map((x) => (x.body as { userId: string }).userId)).toEqual(["u1", "u2"]);
    expect(sent.at(-1)).toEqual({ type: "auth", code: "c2", userId: "u2" });
  });

  it("shows the anonymous visitor and tells the editor when the platform refuses the code", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    const { previewAs, sent, onFailed } = await previewUser();
    platform.on("POST", CODE, status(403));

    await previewAs.pick("u1");

    expect(warn).toHaveBeenCalledOnce();
    expect(onFailed).toHaveBeenCalledWith({ id: "u1", name: "anna" });
    expect(previewAs.userId.value).toBeNull();
    expect(sent).toEqual([{ type: "auth", token: null, userId: null }]);
    warn.mockRestore();
  });

  it("stops answering once the designer is closed", async () => {
    const { previewAs, sent, wrapper } = await previewUser();
    await previewAs.pick("u1");

    previewAs.renew("u1");
    wrapper.unmount();
    await vi.advanceTimersByTimeAsync(CODE_LIFETIME_MS);
    await flushPromises();

    expect(sent).toHaveLength(1);
    expect(platform.to(CODE)).toHaveLength(1);
  });
});
