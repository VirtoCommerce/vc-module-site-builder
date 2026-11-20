import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import test from "node:test";
import { runInNewContext } from "node:vm";

const source = readFileSync(resolve(__dirname, "../../Scripts/blades/pages/edit-page.js"), "utf8");

const anything: unknown = new Proxy(function () {}, {
  get: (_target, key) => (key === Symbol.toPrimitive ? () => "" : anything),
  apply: () => anything,
});

function openPage(currentEntity: Record<string, unknown>) {
  const opened: string[] = [];
  const blade: Record<string, unknown> = { storeId: "B2B", contentType: "pages", toolbarCommands: [], parentBlade: {}, currentEntity };
  const $scope = { blade, $watch: () => undefined, $on: () => undefined };
  const siteBuilderApi = {
    getPage: (_query: unknown, success: (data: unknown) => void) => success({ data: JSON.stringify([{ permalink: "/a" }]) }),
    publishStatus: () => undefined,
    legacyDraft: () => undefined,
  };

  let controller: (...deps: unknown[]) => void = () => undefined;
  const angular = {
    module: () => ({
      controller: (_name: string, definition: unknown[]) => {
        controller = definition.at(-1) as typeof controller;
      },
    }),
    copy: (value: unknown) => JSON.parse(JSON.stringify(value)),
    noop: () => undefined,
  };
  const window = { location: { pathname: "/", origin: "https://admin.test" }, open: (url: string) => opened.push(url) };
  runInNewContext(source, { angular, window, console, setTimeout, clearTimeout });

  const $q = { resolve: (value: unknown) => Promise.resolve(value) };
  controller(
    anything, $scope, $q, anything, { indexedSearchEnabled: () => undefined }, siteBuilderApi, anything,
    anything, anything, anything, anything, anything, anything, () => ({}), anything,
  );

  const command = () => (blade.toolbarCommands as { name: string; executeMethod: () => void }[]).find((x) => x.name === "siteBuilder.commands.open-designer")!;
  return {
    opened,
    press: () => command().executeMethod(),
  };
}

test("the Designer command opens the page in the shell", () => {
  const page = openPage({ name: "a.json", relativeUrl: "/a b.json" });
  page.press();
  assert.deepEqual(page.opened, ["https://admin.test/apps/site-builder-shell/?storeId=B2B&designType=pages&designPath=%2Fa%20b.json"]);
});

test("the Designer command passes the page language", () => {
  const page = openPage({ name: "a.json", relativeUrl: "/a.json", language: "de-DE" });
  page.press();
  assert.deepEqual(page.opened, ["https://admin.test/apps/site-builder-shell/?storeId=B2B&designType=pages&designPath=%2Fa.json&cultureName=de-DE"]);
});
