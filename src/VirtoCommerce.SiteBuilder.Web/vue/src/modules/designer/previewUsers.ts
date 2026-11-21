export const IMPERSONATE_PERMISSION = "platform:security:loginOnBehalf";
export const PREVIEW_USERS_SETTING = "SiteBuilder.Store.PreviewUserIds";

export interface PreviewUser {
  id: string;
  name: string;
}

export function previewUserIds(store: { settings?: { name?: string; value?: unknown }[] } | undefined): string[] {
  const value = store?.settings?.find((x) => x.name === PREVIEW_USERS_SETTING)?.value;
  if (typeof value !== "string") return [];
  return [...new Set(value.split(/\r?\n/).map((x) => x.trim()).filter(Boolean))];
}

export function previewUsers(ids: string[], found: { id?: string; userName?: string; email?: string }[]): PreviewUser[] {
  return ids.map((id) => {
    const user = found.find((x) => x.id === id);
    return { id, name: user?.userName || user?.email || id };
  });
}

export const previewCodeUrl = "/api/site-builder/preview-auth/code";

export const previewCodeBody = (storeId: string, userId: string) => JSON.stringify({ storeId, userId });

export function readPreviewCode(answer: unknown): string | undefined {
  const code = (answer as { code?: unknown } | null | undefined)?.code;
  return typeof code === "string" && code ? code : undefined;
}
