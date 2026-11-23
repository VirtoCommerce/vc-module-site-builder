import type { PageDocument } from "./types";
import { templateKeyOf } from "./templates";
import { byCodeUnit } from "./json";
import type { ThemeStatus } from "./themePublication";

// The storefront takes the draft's templates in place of all the published ones, so a draft has every template or none.

export const draftedTemplateKeys = (status: ThemeStatus | undefined): string[] =>
  [...new Set((status?.drafts ?? []).map((x) => templateKeyOf(x)).filter((x): x is string => !!x))].sort(byCodeUnit);

export interface ThemeFileSource<T> {
  keys: () => Promise<string[]>;
  read: (key: string) => Promise<T | undefined>;
}

/** Live files change only with a publication, so those without a draft are read once per publication. */
export function savedThemeFilesReader<T>(draftedKeys: (status: ThemeStatus) => string[], kept: (file: T) => T, { withoutDraft = false } = {}) {
  let live: { scope: string; keys?: string[]; docs: Map<string, T | undefined> } | undefined;
  return async function read(scope: string, status: ThemeStatus, source: ThemeFileSource<T>): Promise<Record<string, T> | undefined> {
    const drafted = draftedKeys(status);
    if (!drafted.length && !withoutDraft) return undefined;
    const at = `${scope}#${status.publication ?? ""}`;
    if (live?.scope !== at) live = { scope: at, docs: new Map() };
    const cache = live;
    cache.keys ??= await source.keys();
    const keys = [...new Set([...cache.keys, ...drafted])];
    if (!keys.length) return undefined;
    const docs = await Promise.all(
      keys.map(async (key) => {
        if (drafted.includes(key)) return source.read(key);
        if (!cache.docs.has(key)) cache.docs.set(key, await source.read(key));
        return cache.docs.get(key);
      }),
    );
    const files: Record<string, T> = {};
    keys.forEach((key, i) => {
      const doc = docs[i];
      if (doc) files[key] = kept(doc);
    });
    return files;
  };
}

export const savedTemplatesReader = () =>
  savedThemeFilesReader<PageDocument>(draftedTemplateKeys, (doc) => ({ settings: doc.settings, content: doc.content }));

export function draftTemplates(
  saved: Record<string, PageDocument> | undefined,
  open?: { key: string; document: PageDocument },
): Record<string, PageDocument> | undefined {
  if (!saved) return undefined;
  return open ? { ...saved, [open.key]: { settings: open.document.settings, content: open.document.content } } : saved;
}
