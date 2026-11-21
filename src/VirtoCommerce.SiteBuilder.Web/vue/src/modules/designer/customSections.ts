import type { Field, ItemSchema } from "./types";
import { byCodeUnit } from "./json";
import { isTranslations, resolveTranslations } from "./translatable";
import { savedThemeFilesReader } from "./templateDrafts";
import type { ThemeStatus } from "./themePublication";

/** custom-sections/{type}.json as the file holds it; the preview takes it as is. */
export interface CustomSectionType {
  format: 1;
  name: string | Record<string, string>;
  schema: { settings: Field[]; targetTemplates?: string[]; limit?: number; icon?: string };
  template: string;
  css: string;
}

const TYPE = /^custom-[a-z0-9]+(?:-[a-z0-9]+)*$/;
const TYPE_MAX_LENGTH = 64;

const isCustomSectionType = (type: string) => type.length <= TYPE_MAX_LENGTH && TYPE.test(type);

export const customSectionPath = (theme: string, type: string) => `${theme}/custom-sections/${type}.json`;

export function customSectionTypeOf(path: string): string | undefined {
  const type = /(?:^|\/)custom-sections\/([^/]+)\.json(?:-draft)?$/.exec(path)?.[1];
  return type && isCustomSectionType(type) ? type : undefined;
}

export const draftedCustomSectionTypes = (status: ThemeStatus | undefined): string[] =>
  [...new Set((status?.drafts ?? []).map(customSectionTypeOf).filter((x): x is string => !!x))].sort(byCodeUnit);

/** Like the templates: the storefront takes the draft's types in place of all the published ones. */
export const savedCustomSectionsReader = () => savedThemeFilesReader<CustomSectionType>(draftedCustomSectionTypes, (type) => type);

/** A custom type's name may be a dictionary by language; the designer shows the one for its own language. */
export function namedSections(sections: Record<string, ItemSchema>, language: string, defaultLanguage: string): Record<string, ItemSchema> {
  return Object.fromEntries(
    Object.entries(sections).map(([type, schema]) => {
      const name: unknown = schema.name;
      if (name === undefined || typeof name === "string") return [type, schema];
      return [type, { ...schema, name: isTranslations(name) ? resolveTranslations(name, language, defaultLanguage) : undefined }];
    }),
  );
}
