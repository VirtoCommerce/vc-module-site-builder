import assert from "node:assert/strict";
import test from "node:test";
import { markdownOf, markdownToHtml, markdownValue, resultTypeOf, translationText } from "../src/modules/designer/markdown";

test("the result type is markdown, html or, for anything else, mixed", () => {
  assert.equal(resultTypeOf({ type: "markdown", resultType: "markdown" }), "markdown");
  assert.equal(resultTypeOf({ type: "markdown", resultType: "html" }), "html");
  assert.equal(resultTypeOf({ type: "markdown" }), "mixed");
  assert.equal(resultTypeOf({ type: "markdown", resultType: "other" }), "mixed");
});

test("without a DOM to sanitize with, raw HTML is escaped rather than kept", () => {
  assert.equal(markdownToHtml("<script>alert(1)</script>"), "<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>\n");
  assert.equal(markdownToHtml("<img src=x onerror=alert(1)>"), "<p>&lt;img src=x onerror=alert(1)&gt;</p>\n");
  assert.equal(markdownToHtml("[x](javascript:alert(1))"), "<p>[x](javascript:alert(1))</p>\n");
  assert.equal(markdownToHtml("**a**"), "<p><strong>a</strong></p>\n");
});

test("the text to edit is the markdown, else the HTML", () => {
  assert.equal(markdownOf("a"), "a");
  assert.equal(markdownOf({ markdown: "a", html: "<p>a</p>" }), "a");
  assert.equal(markdownOf({ markdown: null, html: "<p>a</p>" }), "<p>a</p>");
  assert.equal(markdownOf({ html: "<p>a</p>" }), "<p>a</p>");
  assert.equal(markdownOf(null), "");
  assert.equal(markdownOf(4), "");
});

test("an edited text is stored in the field's shape", () => {
  assert.equal(markdownValue("**a**", "markdown"), "**a**");
  assert.equal(markdownValue("**a**", "html"), "<p><strong>a</strong></p>\n");
  assert.deepEqual(markdownValue("**a**", "mixed"), { markdown: "**a**", html: "<p><strong>a</strong></p>\n" });
  assert.deepEqual(markdownValue("", "mixed"), { markdown: null, html: null });
  assert.equal(markdownValue("", "html"), null);
});

test("a language's text is the HTML, or the markdown for a markdown-only field", () => {
  assert.equal(translationText({ markdown: "**a**", html: "<p>A</p>" }, "mixed"), "<p>A</p>");
  assert.equal(translationText({ markdown: "**a**", html: null }, "html"), "<p><strong>a</strong></p>\n");
  assert.equal(translationText({ markdown: "**a**", html: "<p>A</p>" }, "markdown"), "**a**");
  assert.equal(translationText("a", "mixed"), "a");
  assert.equal(translationText(null, "mixed"), "");
});
