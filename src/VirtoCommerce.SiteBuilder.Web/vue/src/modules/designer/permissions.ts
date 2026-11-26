import type { CustomSectionPart } from "./customSections";
import type { HistoryFile } from "./history";

// Mirrors the permissions the server checks on a designer Save, Discard and history Restore: the update permission, then each part's own.

export const PERMISSIONS = {
  update: "builder:update",
  templates: "builder:templates",
  theme: "builder:theme",
  publish: "builder:publish",
  promote: "builder:promote",
} as const;

export function partPermission(part: HistoryFile | CustomSectionPart, template: boolean): string {
  if (part === "page") return template ? PERMISSIONS.templates : PERMISSIONS.update;
  return part === "rules" ? PERMISSIONS.templates : PERMISSIONS.theme;
}

export const refusedParts = <T extends HistoryFile | CustomSectionPart>(parts: T[], template: boolean, hasAccess: (permission: string) => boolean): T[] =>
  [...new Set(parts)].filter((part) => !hasAccess(PERMISSIONS.update) || !hasAccess(partPermission(part, template)));
