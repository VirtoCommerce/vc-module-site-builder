import type { Field, ItemSchema } from "./types";
import { byCodeUnit, isRecord } from "./json";
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

/** A custom section type: its key always starts with `custom-`, which no other theme part does. */
export type CustomSectionPart = `custom-${string}`;

export const isCustomSectionPart = (type: string): type is CustomSectionPart => type.length <= TYPE_MAX_LENGTH && TYPE.test(type);

export const customSectionPath = (theme: string, type: string) => `${theme}/custom-sections/${type}.json`;

export function customSectionTypeOf(path: string): CustomSectionPart | undefined {
  const type = /(?:^|\/)custom-sections\/([^/]+)\.json(?:-draft)?$/.exec(path)?.[1];
  return type && isCustomSectionPart(type) ? type : undefined;
}

export const draftedCustomSectionTypes = (status: ThemeStatus | undefined): CustomSectionPart[] =>
  [...new Set((status?.drafts ?? []).map(customSectionTypeOf).filter((x): x is CustomSectionPart => !!x))].sort(byCodeUnit);

/**
 * The storefront takes the draft's types in place of all the published ones. They go while the theme has any, drafted or
 * not: the snapshot holds them in one language, and the designer takes a type's defaults from what the preview holds.
 * A type that cannot be read costs only itself, as the first publication leaves out a type that is not JSON.
 */
export const savedCustomSectionsReader = () =>
  savedThemeFilesReader<CustomSectionType>(draftedCustomSectionTypes, (type) => type, { withoutDraft: true, skipUnreadable: true });

// As the server's save takes them (CustomSectionValidator.FieldKeys, FieldTypes): a type file written past it, such as through
// the content API, would otherwise give the form a field that runs requests with the editor's session.
const FIELD_KEYS = new Set(["id", "type", "label", "default", "translatable", "tab", "info", "placeholder", "required", "multiline", "multiple", "format", "options", "element", "min", "max", "step"]);
const FIELD_TYPES = new Set(["header", "paragraph", "string", "text", "number", "slider", "checkbox", "select", "color-token", "images", "calendar", "list", "object"]);
const SCHEMA_KEYS = ["name", "custom", "settings", "targetTemplates", "limit", "icon", "excludeShared"] as const;

function keptFields(fields: unknown): Field[] {
  return (Array.isArray(fields) ? fields : [])
    .filter((field): field is Field => isRecord(field) && typeof field.type === "string" && FIELD_TYPES.has(field.type))
    .map((field) => {
      const kept = Object.fromEntries(Object.entries(field).filter(([key]) => FIELD_KEYS.has(key))) as unknown as Field;
      return field.element ? { ...kept, element: keptFields(field.element) } : kept;
    });
}

/** The custom types of a catalog with only what a custom type may declare. */
export function keptCustomSections(sections: Record<string, ItemSchema>): Record<string, ItemSchema> {
  return Object.fromEntries(
    Object.entries(sections).map(([type, schema]) => {
      if (!schema.custom) return [type, schema];
      const kept = Object.fromEntries(SCHEMA_KEYS.filter((key) => key in schema).map((key) => [key, schema[key]])) as ItemSchema;
      return [type, { ...kept, settings: keptFields(schema.settings) }];
    }),
  );
}

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
