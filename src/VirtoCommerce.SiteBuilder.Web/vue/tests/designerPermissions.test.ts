import assert from "node:assert/strict";
import test from "node:test";
import { discardChoice, discardOptions, removedSectionTypes } from "../src/modules/designer/discard";
import { PERMISSIONS, partPermission, refusedParts } from "../src/modules/designer/permissions";

const only =
  (...granted: string[]) =>
  (permission: string) =>
    granted.includes(permission);

test("each part is saved and discarded with the permission the server checks for it", () => {
  assert.equal(partPermission("page", false), PERMISSIONS.update);
  assert.equal(partPermission("page", true), PERMISSIONS.templates);
  assert.equal(partPermission("rules", false), PERMISSIONS.templates);
  for (const part of ["header", "footer", "brand", "palette", "darkPalette"] as const) assert.equal(partPermission(part, false), PERMISSIONS.theme);
});

test("a refused Save names the parts the editor may not save, once each", () => {
  assert.deepEqual(refusedParts(["page", "header", "rules", "header", "brand"], false, only(PERMISSIONS.update)), ["header", "rules", "brand"]);
  assert.deepEqual(refusedParts(["page", "rules"], true, only(PERMISSIONS.update, PERMISSIONS.theme)), ["page", "rules"]);
  assert.deepEqual(refusedParts(["page"], false, only(PERMISSIONS.update)), []);
});

test("without builder:update every part is refused, since the server checks it on Save, Discard and Restore first", () => {
  assert.deepEqual(refusedParts(["header", "brand", "palette"], false, only(PERMISSIONS.theme)), ["header", "brand", "palette"]);
  assert.deepEqual(refusedParts(["page", "rules"], true, only(PERMISSIONS.templates)), ["page", "rules"]);
  assert.deepEqual(discardOptions({ page: false, theme: ["header", "brand"], template: false, hasAccess: only(PERMISSIONS.theme) }), [
    { part: "header", checked: false, allowed: false },
    { part: "brand", checked: false, allowed: false },
  ]);
});

test("Discard offers the page's draft ticked and each theme part's unticked; only what the editor may edit can be ticked", () => {
  const options = discardOptions({ page: true, theme: ["rules", "header", "brand"], template: false, hasAccess: only(PERMISSIONS.update, PERMISSIONS.theme) });
  assert.deepEqual(options, [
    { part: "page", checked: true, allowed: true },
    { part: "rules", checked: false, allowed: false },
    { part: "header", checked: false, allowed: true },
    { part: "brand", checked: false, allowed: true },
  ]);
  assert.deepEqual(discardOptions({ page: true, theme: [], template: true, hasAccess: only(PERMISSIONS.update) }), [{ part: "page", checked: false, allowed: false }]);
  assert.deepEqual(discardOptions({ page: false, theme: [], template: false, hasAccess: only() }), []);
});

test("Discard sends only the parts ticked that it offered and the editor may discard", () => {
  const options = discardOptions({ page: true, theme: ["rules", "header"], template: false, hasAccess: only(PERMISSIONS.update, PERMISSIONS.theme) });
  assert.deepEqual(discardChoice(options, ["page"]), { page: true, theme: [] });
  assert.deepEqual(discardChoice(options, ["header", "rules", "footer"]), { page: false, theme: ["header"] });
  assert.deepEqual(discardChoice(options, []), { page: false, theme: [] });
});

test("Discard offers each custom section type's draft unticked, by its name, saying which were never published", () => {
  const options = discardOptions({
    page: false,
    theme: ["header", "custom-numbers", "custom-hero"],
    template: false,
    hasAccess: only(PERMISSIONS.update, PERMISSIONS.theme),
    sectionName: (type) => (type === "custom-numbers" ? "Roastery in numbers" : undefined),
    neverPublished: ["custom-numbers"],
  });
  assert.deepEqual(options, [
    { part: "header", checked: false, allowed: true },
    { part: "custom-numbers", checked: false, allowed: true, name: "Roastery in numbers", removes: true },
    { part: "custom-hero", checked: false, allowed: true, name: "custom-hero", removes: false },
  ]);
  assert.deepEqual(
    discardOptions({ page: false, theme: ["custom-hero"], template: false, hasAccess: only(PERMISSIONS.update, PERMISSIONS.templates) }),
    [{ part: "custom-hero", checked: false, allowed: false, name: "custom-hero", removes: false }],
  );
});

test("Discard sends the custom section types ticked with the theme parts, and names those it removes for good", () => {
  const options = discardOptions({
    page: true,
    theme: ["header", "custom-numbers", "custom-hero"],
    template: false,
    hasAccess: only(PERMISSIONS.update, PERMISSIONS.theme),
    neverPublished: ["custom-numbers"],
  });
  assert.deepEqual(discardChoice(options, ["custom-numbers", "custom-hero", "custom-other"]), { page: false, theme: ["custom-numbers", "custom-hero"] });
  assert.deepEqual(removedSectionTypes(options, ["header", "custom-hero"]), []);
  assert.deepEqual(
    removedSectionTypes(options, ["custom-hero", "custom-numbers"]).map((x) => x.part),
    ["custom-numbers"],
  );
  const refused = discardOptions({ page: false, theme: ["custom-numbers"], template: false, hasAccess: only(PERMISSIONS.update), neverPublished: ["custom-numbers"] });
  assert.deepEqual(removedSectionTypes(refused, ["custom-numbers"]), []);
});
