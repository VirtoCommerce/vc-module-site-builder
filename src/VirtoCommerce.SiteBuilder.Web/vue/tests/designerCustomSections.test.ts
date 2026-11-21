import assert from "node:assert/strict";
import test from "node:test";
import {
  customSectionPath,
  customSectionTypeOf,
  draftedCustomSectionTypes,
  namedSections,
  savedCustomSectionsReader,
  type CustomSectionType,
} from "../src/modules/designer/customSections";
import type { ThemeFileSource } from "../src/modules/designer/templateDrafts";
import type { ThemeStatus } from "../src/modules/designer/themePublication";
import type { ItemSchema } from "../src/modules/designer/types";

const type = (name: string): CustomSectionType => ({ format: 1, name, schema: { settings: [] }, template: "<section></section>", css: "" });
const status = (drafts: string[], publication?: number): ThemeStatus => ({ publication, drafts, newDrafts: [] });

function source(files: Record<string, CustomSectionType | undefined>) {
  const requests: string[] = [];
  const src: ThemeFileSource<CustomSectionType> = {
    keys: async () => {
      requests.push("keys");
      return Object.keys(files);
    },
    read: async (key) => {
      requests.push(key);
      return files[key];
    },
  };
  return { src, requests, files };
}

test("a custom section type is the name of its file in the theme's custom-sections folder", () => {
  assert.equal(customSectionPath("default", "custom-hero"), "default/custom-sections/custom-hero.json");
  assert.equal(customSectionTypeOf("default/custom-sections/custom-hero.json"), "custom-hero");
  assert.equal(customSectionTypeOf("custom-sections/custom-hero-2.json-draft"), "custom-hero-2");
  for (const path of ["custom-sections/hero.json", "custom-sections/Custom-Hero.json", "custom-sections/custom-hero.css", "templates/custom-hero.json", `custom-sections/custom-${"a".repeat(58)}.json`])
    assert.equal(customSectionTypeOf(path), undefined, path);
});

test("draftedCustomSectionTypes: the custom types among the theme files with a draft", () => {
  assert.deepEqual(
    draftedCustomSectionTypes(status(["templates/product.json", "custom-sections/custom-promo.json", "config/brand.json", "custom-sections/custom-hero.json"])),
    ["custom-hero", "custom-promo"],
  );
  assert.deepEqual(draftedCustomSectionTypes(status(["templates/product.json"])), []);
  assert.deepEqual(draftedCustomSectionTypes(undefined), []);
});

test("no custom type with a draft: none, and nothing read (the published snapshot has them all)", async () => {
  const { src, requests } = source({ "custom-hero": type("Hero") });
  assert.equal(await savedCustomSectionsReader()("s/default", status(["templates/product.json"], 3), src), undefined);
  assert.deepEqual(requests, []);
});

test("a custom type with a draft: every type goes, each as its file holds it, a new one not listed yet among them", async () => {
  const { src, files } = source({ "custom-hero": type("Hero"), "custom-promo": type("Promo"), "custom-gone": undefined });
  src.read = async (key) => (key === "custom-new" ? type("New") : files[key]);
  const types = await savedCustomSectionsReader()("s/default", status(["custom-sections/custom-hero.json", "custom-sections/custom-new.json"], 3), src);
  assert.deepEqual(types, { "custom-hero": type("Hero"), "custom-promo": type("Promo"), "custom-new": type("New") });
});

test("live custom types are read once per publication, drafts on every read", async () => {
  const { src, requests } = source({ "custom-hero": type("Hero"), "custom-promo": type("Promo") });
  const read = savedCustomSectionsReader();
  await read("s/default", status(["custom-sections/custom-hero.json"], 3), src);
  requests.length = 0;
  await read("s/default", status(["custom-sections/custom-hero.json"], 3), src);
  assert.deepEqual(requests, ["custom-hero"]);
});

test("a custom type's name by language is shown in the designer's language, else the store's default, else its first", () => {
  const name = { "en-US": "Hero", "de-DE": "Held" } as unknown as string;
  const sections: Record<string, ItemSchema> = { text: { name: "Text" }, "custom-hero": { name, custom: true }, "custom-plain": { custom: true } };
  assert.deepEqual(namedSections(sections, "de", "en-US"), { text: { name: "Text" }, "custom-hero": { name: "Held", custom: true }, "custom-plain": { custom: true } });
  assert.equal(namedSections(sections, "fr", "de-DE")["custom-hero"].name, "Held");
  assert.equal(namedSections(sections, "fr", "")["custom-hero"].name, "Hero");
  assert.equal(namedSections({ odd: { name: { title: "Hero" } as unknown as string } }, "en", "en-US").odd.name, undefined);
});

test("a type never published is gone from the draft once its draft is discarded, though the folder was listed with it", async () => {
  const { src, files } = source({ "custom-hero": type("Hero"), "custom-new": type("New") });
  const read = savedCustomSectionsReader();
  const drafted = status(["custom-sections/custom-hero.json", "custom-sections/custom-new.json"], 3);
  assert.deepEqual(Object.keys((await read("s/default", drafted, src)) ?? {}), ["custom-hero", "custom-new"]);

  delete files["custom-new"];

  assert.deepEqual(Object.keys((await read("s/default", status(["custom-sections/custom-hero.json"], 3), src)) ?? {}), ["custom-hero"]);
  assert.equal(await read("s/default", status([], 3), src), undefined);
});
