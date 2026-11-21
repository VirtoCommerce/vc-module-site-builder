import type { Field } from "./types";
import { byCodeUnit } from "./json";
import { savedThemeFilesReader } from "./templateDrafts";
import type { ThemeStatus } from "./themePublication";

/** A section type an agent writes into the theme; the designer passes it to the preview as the file holds it. */
export interface CustomSectionType {
  format: 1;
  name: string | Record<string, string>;
  schema: { settings: Field[]; targetTemplates?: string[]; limit?: number; icon?: string };
  template: string;
  css: string;
}

const TYPE = /^custom-[a-z0-9]+(?:-[a-z0-9]+)*$/;
const TYPE_MAX_LENGTH = 64;

export const isCustomSectionType = (type: string) => type.length <= TYPE_MAX_LENGTH && TYPE.test(type);

export const customSectionPath = (theme: string, type: string) => `${theme}/custom-sections/${type}.json`;

export function customSectionTypeOf(path: string): string | undefined {
  const type = /(?:^|\/)custom-sections\/([^/]+)\.json(?:-draft)?$/.exec(path)?.[1];
  return type && isCustomSectionType(type) ? type : undefined;
}

export const draftedCustomSectionTypes = (status: ThemeStatus | undefined): string[] =>
  [...new Set((status?.drafts ?? []).map(customSectionTypeOf).filter((x): x is string => !!x))].sort(byCodeUnit);

/** Like the templates: the storefront takes the draft's types in place of all the published ones. */
export const savedCustomSectionsReader = () => savedThemeFilesReader<CustomSectionType>(draftedCustomSectionTypes, (type) => type);
