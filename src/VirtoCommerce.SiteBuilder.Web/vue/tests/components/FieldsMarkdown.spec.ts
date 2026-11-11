import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import { VcTextarea } from "./support/vcShellUi";
import Fields from "../../src/modules/designer/components/Fields.vue";
import { markdownToHtml } from "../../src/modules/designer/markdown";
import type { Field } from "../../src/modules/designer/types";
import type { Translating } from "../../src/modules/designer/translatable";

const MIXED: Field = { id: "text", type: "markdown", label: "Content" };
const MARKDOWN: Field = { ...MIXED, resultType: "markdown" };
const HTML: Field = { ...MIXED, resultType: "html" };

const TEXT = "Every order ships from our own warehouse.\n\n- **Free delivery** on orders over $500\n- Net-30";
// What the classic designer stored for the same text.
const TEXT_HTML = "<p>Every order ships from our own warehouse.</p>\n<ul>\n<li><strong>Free delivery</strong> on orders over $500</li>\n<li>Net-30</li>\n</ul>\n";

const mountField = (field: Field, value: unknown, translation?: Translating) =>
  mount(Fields, { props: { fields: [field], values: { [field.id!]: value }, context: {}, translation } });
type Wrapper = ReturnType<typeof mountField>;
const shown = (wrapper: Wrapper) => (wrapper.find("textarea").element as HTMLTextAreaElement).value;
const lastSaved = (wrapper: Wrapper) => wrapper.emitted("update")!.at(-1)![1];
async function type(wrapper: Wrapper, text: string) {
  // An edit that ends on the text shown, so emitted even when the text is the same.
  wrapper.findComponent(VcTextarea).vm.$emit("update:modelValue", text);
  const field = (wrapper.props("fields") as Field[])[0];
  await wrapper.setProps({ values: { [field.id!]: lastSaved(wrapper) } });
}

describe("a markdown field saves the shape its schema asks for", () => {
  it("mixed, when the schema names no result type: the markdown and its HTML", async () => {
    const wrapper = mountField(MIXED, undefined);
    await type(wrapper, TEXT);
    expect(lastSaved(wrapper)).toEqual({ markdown: TEXT, html: TEXT_HTML });
  });

  it("markdown: the markdown alone", async () => {
    const wrapper = mountField(MARKDOWN, undefined);
    await type(wrapper, TEXT);
    expect(lastSaved(wrapper)).toBe(TEXT);
  });

  it("html: the HTML alone, while the textarea keeps the typed markdown", async () => {
    const wrapper = mountField(HTML, undefined);
    await type(wrapper, TEXT);
    expect(lastSaved(wrapper)).toBe(TEXT_HTML);
    expect(shown(wrapper)).toBe(TEXT);
  });

  it("an emptied text is saved empty in each shape", async () => {
    for (const [field, empty] of [[MIXED, { markdown: null, html: null }], [MARKDOWN, null], [HTML, null]] as const) {
      const wrapper = mountField(field, TEXT);
      await type(wrapper, "");
      expect(lastSaved(wrapper)).toEqual(empty);
    }
  });

  it("a change from elsewhere, an undo for one, replaces the typed markdown", async () => {
    const wrapper = mountField(HTML, undefined);
    await type(wrapper, "**a**");
    await wrapper.setProps({ values: { text: "<p>restored</p>" } });
    expect(shown(wrapper)).toBe("<p>restored</p>");
  });
});

describe("a markdown field reads what is stored", () => {
  it.each<[string, unknown, string]>([
    ["a string", TEXT, TEXT],
    ["markdown with its HTML", { markdown: TEXT, html: TEXT_HTML }, TEXT],
    ["HTML only", { html: TEXT_HTML }, TEXT_HTML],
    ["nothing", null, ""],
  ])("%s", (_, value, text) => {
    expect(shown(mountField(MIXED, value))).toBe(text);
  });

  it("HTML stored without its markdown converts back to the same HTML", async () => {
    const wrapper = mountField(MIXED, { html: TEXT_HTML });
    await type(wrapper, shown(wrapper));
    expect(lastSaved(wrapper)).toEqual({ markdown: TEXT_HTML, html: TEXT_HTML });
  });

  it("a saved value shows its markdown again and saves unchanged", async () => {
    const first = mountField(MIXED, undefined);
    await type(first, TEXT);
    const saved = lastSaved(first);
    const again = mountField(MIXED, saved);
    expect(shown(again)).toBe(TEXT);
    await type(again, shown(again));
    expect(lastSaved(again)).toEqual(saved);
  });
});

describe("a translatable markdown field", () => {
  const field: Field = { ...MIXED, translatable: true };
  const at = (language: string): Translating => ({ language, defaultLanguage: "en-US", languages: ["en-US", "de-DE"] });

  it("keeps its shape while only the default language has a text", async () => {
    const wrapper = mountField(field, undefined, at("en-US"));
    await type(wrapper, TEXT);
    expect(lastSaved(wrapper)).toEqual({ markdown: TEXT, html: TEXT_HTML });
  });

  it("holds each language's HTML once translated, as a dictionary holds strings", async () => {
    const wrapper = mountField(field, { markdown: TEXT, html: TEXT_HTML }, at("de-DE"));
    expect(shown(wrapper)).toBe("");
    expect(wrapper.find("textarea").attributes("placeholder")).toBe(TEXT);
    await type(wrapper, "**Kostenlose** Lieferung");
    expect(lastSaved(wrapper)).toEqual({ "en-US": TEXT_HTML, "de-DE": "<p><strong>Kostenlose</strong> Lieferung</p>\n" });
    expect(shown(wrapper)).toBe("**Kostenlose** Lieferung");
  });
});

describe("markdown to HTML", () => {
  it("follows the classic designer's converter: GitHub flavour, no line breaks, heading ids, links", () => {
    expect(markdownToHtml("# Hello World\n\n## Hello World\n\nline1\nline2 ~~gone~~ www.x.com <https://y.com>")).toBe(
      '<h1 id="hello-world">Hello World</h1>\n<h2 id="hello-world-1">Hello World</h2>\n' +
        '<p>line1\nline2 <del>gone</del> <a href="http://www.x.com">www.x.com</a> <a href="https://y.com">https://y.com</a></p>\n',
    );
  });

  it("keeps harmless raw HTML", () => {
    expect(markdownToHtml('a <span class="note">b</span> <a href="/p" target="_blank">c</a>')).toBe(
      '<p>a <span class="note">b</span> <a href="/p" target="_blank">c</a></p>\n',
    );
  });

  it.each([
    ["<script>alert(1)</script>", ""],
    ["<img src=x onerror=alert(1)>", '<img src="x">'],
    ["[x](javascript:alert(1))", "<p>[x](javascript:alert(1))</p>\n"],
    ['<a href="javascript:alert(1)">x</a>', "<p><a>x</a></p>\n"],
    ['<iframe src="https://example.com"></iframe>', ""],
    ['text <svg onload="alert(1)"></svg>', "<p>text </p>\n"],
  ])("strips what runs script: %s", (markdown, html) => {
    expect(markdownToHtml(markdown)).toBe(html);
  });
});
