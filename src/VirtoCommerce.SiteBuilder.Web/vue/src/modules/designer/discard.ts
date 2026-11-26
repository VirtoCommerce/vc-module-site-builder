import type { HistoryFile, ThemePart } from "./history";
import { isCustomSectionPart, type CustomSectionPart } from "./customSections";
import { refusedParts } from "./permissions";

// A theme part's draft is shared by every page and maybe saved by someone else, so the editor picks which ones go.

export type DiscardThemePart = ThemePart | CustomSectionPart;
export type DiscardPart = HistoryFile | CustomSectionPart;

export interface DiscardOption {
  part: DiscardPart;
  checked: boolean;
  allowed: boolean;
  /** a custom section type's name, as the section picker shows it */
  name?: string;
  /** a custom section type never published: its draft is the only copy, so discarding it deletes the type */
  removes?: boolean;
}

export function discardOptions(input: {
  page: boolean;
  theme: DiscardThemePart[];
  template: boolean;
  hasAccess: (permission: string) => boolean;
  sectionName?: (type: CustomSectionPart) => string | undefined;
  neverPublished?: CustomSectionPart[];
}): DiscardOption[] {
  const allowed = (part: DiscardPart) => !refusedParts([part], input.template, input.hasAccess).length;
  const page: DiscardOption[] = input.page ? [{ part: "page", checked: allowed("page"), allowed: allowed("page") }] : [];
  return [
    ...page,
    ...input.theme.map((part) =>
      isCustomSectionPart(part)
        ? { part, checked: false, allowed: allowed(part), name: input.sectionName?.(part) || part, removes: !!input.neverPublished?.includes(part) }
        : { part, checked: false, allowed: allowed(part) },
    ),
  ];
}

const picked = (options: DiscardOption[], ticked: DiscardPart[]) => options.filter((x) => x.allowed && ticked.includes(x.part));

export function discardChoice(options: DiscardOption[], ticked: DiscardPart[]): { page: boolean; theme: DiscardThemePart[] } {
  const parts = picked(options, ticked).map((x) => x.part);
  return { page: parts.includes("page"), theme: parts.filter((x): x is DiscardThemePart => x !== "page") };
}

export const removedSectionTypes = (options: DiscardOption[], ticked: DiscardPart[]): DiscardOption[] => picked(options, ticked).filter((x) => x.removes);
