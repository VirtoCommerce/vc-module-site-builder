import { computed, onBeforeUnmount, ref, shallowRef } from "vue";
import { useLocalStorage } from "@vueuse/core";
import { usePermissions } from "@vc-shell/framework";
import { loadPreviewUsers, previewCode } from "./api";
import { ANONYMOUS, authMessage, type AuthMessage } from "./previewMessages";
import { IMPERSONATE_PERMISSION, type PreviewUser } from "./previewUsers";

/** As long as a code lives: a renewal asked for sooner means the storefront could not exchange the last code. */
export const CODE_LIFETIME_MS = 60_000;

/** The waits before answering renewals after failed exchanges; one more failure shows the anonymous visitor. */
export const FAILED_RENEWAL_DELAYS_MS = [1_000, 5_000, 30_000];

/**
 * Who the preview renders as. A code is used once, so every `auth` for a user carries a new one; the storefront asks for the next
 * with `authRenew` before its token expires or when the exchange failed.
 */
export function usePreviewUser(options: { send: (message: AuthMessage) => void; onFailed: (user: PreviewUser | undefined) => void }) {
  const { hasAccess } = usePermissions();
  const allowed = computed(() => hasAccess(IMPERSONATE_PERMISSION));
  const users = shallowRef<PreviewUser[]>([]);
  const userId = ref<string | null>(null);
  const user = computed(() => users.value.find((x) => x.id === userId.value));
  const picks = useLocalStorage<Record<string, string>>("vc-page-designer-preview-as", {});

  let storeId: string | undefined;
  let message: AuthMessage = ANONYMOUS;
  let requests = 0;
  let issuedAt = Number.NEGATIVE_INFINITY;
  let failedRenewals = 0;
  let renewal: ReturnType<typeof setTimeout> | undefined;
  let disposed = false;
  onBeforeUnmount(() => {
    disposed = true;
    requests++;
    clearTimeout(renewal);
  });

  async function load(store: string) {
    storeId = store;
    users.value = allowed.value ? await loadPreviewUsers(store).catch(() => []) : [];
    const saved = picks.value[store];
    userId.value = saved && users.value.some((x) => x.id === saved) ? saved : null;
    // A preview that connected meanwhile is rendering as the anonymous visitor, so it gets the message now.
    if (userId.value && (await refresh())) options.send(message);
  }

  /** A new message for the user picked, with a new code: the last one may have been exchanged already. */
  async function refresh(): Promise<boolean> {
    if (disposed) return false;
    const request = ++requests;
    const id = userId.value;
    let next: AuthMessage = ANONYMOUS;
    if (id && storeId) {
      try {
        issuedAt = Date.now();
        next = authMessage(id, await previewCode(storeId, id));
      } catch (e) {
        if (request !== requests) return false;
        console.warn("[site-builder] The preview could not be shown as the user", e);
        options.onFailed(user.value);
        choose(null);
      }
    }
    if (request !== requests) return false;
    message = next;
    return true;
  }

  /**
   * Answers the storefront's `authRenew`, unless the editor has picked someone else since. A storefront whose exchange keeps failing
   * is answered a few times, ever later, then shown the anonymous visitor.
   */
  function renew(id: string) {
    if (disposed || id !== userId.value || renewal) return;
    const failed = Date.now() - issuedAt <= CODE_LIFETIME_MS;
    if (!failed) failedRenewals = 0;
    else if (failedRenewals >= FAILED_RENEWAL_DELAYS_MS.length) return giveUp();
    const delay = failed ? FAILED_RENEWAL_DELAYS_MS[failedRenewals++] : 0;
    renewal = setTimeout(async () => {
      renewal = undefined;
      if (id === userId.value && (await refresh())) options.send(message);
    }, delay);
  }

  function giveUp() {
    console.warn("[site-builder] The storefront could not exchange the preview code; it shows the anonymous visitor");
    requests++;
    options.onFailed(user.value);
    choose(null);
    message = ANONYMOUS;
    options.send(message);
  }

  function choose(id: string | null) {
    userId.value = id;
    if (storeId) picks.value = { ...picks.value, [storeId]: id ?? "" };
  }

  async function pick(id: string | null) {
    if (id === userId.value) return;
    failedRenewals = 0;
    choose(id);
    if (await refresh()) options.send(message);
  }

  return { allowed, users, userId, user, load, refresh, renew, pick, message: () => message };
}
