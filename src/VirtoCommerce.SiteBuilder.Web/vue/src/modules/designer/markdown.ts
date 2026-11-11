import MarkdownIt, { type StateCore } from "markdown-it";
import DOMPurify from "dompurify";
import { isRecord } from "./json";
import type { Field } from "./types";

/** What a `markdown` setting stores: its markdown, its HTML, or both. */
export type MarkdownResult = "markdown" | "html" | "mixed";

export interface MarkdownModel {
  markdown: string | null;
  html: string | null;
}

export const resultTypeOf = (field: Field): MarkdownResult =>
  field.resultType === "markdown" || field.resultType === "html" ? field.resultType : "mixed";

// Heading ids as the classic designer's converter gave them, so that links to a heading keep working.
const slugOf = (text: string) =>
  text
    .toLowerCase()
    .trim()
    .replace(/<[!/a-z].*?>/gi, "")
    .replace(/[ -⁯⸀-⹿\\'!"#$%&()*+,./:;<=>?@[\]^`{|}~]/g, "")
    .replace(/\s/g, "-");

function nextSlug(original: string, seen: Map<string, number>): string {
  let slug = original;
  let count = 0;
  if (seen.has(slug)) {
    count = seen.get(original)!;
    do slug = `${original}-${++count}`;
    while (seen.has(slug));
  }
  seen.set(original, count);
  seen.set(slug, 0);
  return slug;
}

const PLAIN_TEXT = new Set(["text", "code_inline", "html_inline", "image"]);

function headingIds(state: StateCore) {
  const seen = new Map<string, number>();
  state.tokens.forEach((token, i) => {
    if (token.type !== "heading_open") return;
    const text = (state.tokens[i + 1]?.children ?? []).map((x) => (PLAIN_TEXT.has(x.type) ? x.content : "")).join("");
    token.attrSet("id", nextSlug(slugOf(text), seen));
  });
}

function converter(html: boolean): MarkdownIt {
  const md = new MarkdownIt({ html, linkify: true });
  md.core.ruler.push("heading_ids", headingIds);
  md.renderer.rules.s_open = () => "<del>";
  md.renderer.rules.s_close = () => "</del>";
  return md;
}

// The classic designer kept raw HTML, scripts and javascript: links included. Raw HTML still passes, then everything
// that can run script is stripped: the stored HTML reaches clients that may not sanitize it.
const withHtml = converter(true);
// Without a DOM there is nothing to sanitize with, so raw HTML is escaped instead of passing through.
const escapingHtml = converter(false);
const SANITIZE = { USE_PROFILES: { html: true }, ADD_ATTR: ["target"] };

export function markdownToHtml(markdown: string): string {
  if (!DOMPurify.isSupported) return escapingHtml.render(markdown);
  return DOMPurify.sanitize(withHtml.render(markdown), SANITIZE);
}

const textIn = (value: unknown) => (typeof value === "string" ? value : "");

/** A value without markdown is edited as its HTML, which converts back to itself. */
export function markdownOf(value: unknown): string {
  if (typeof value === "string") return value;
  if (!isRecord(value)) return "";
  return textIn(value.markdown) || textIn(value.html);
}

export function markdownValue(text: string, type: MarkdownResult): string | null | MarkdownModel {
  const markdown = text || null;
  if (type === "markdown") return markdown;
  const html = markdown ? markdownToHtml(markdown) : null;
  return type === "html" ? html : { markdown, html };
}

/** A language's text in a dictionary of translations is a string: the HTML, unless the field stores markdown only. */
export function translationText(value: unknown, type: MarkdownResult): string {
  if (typeof value === "string" || !isRecord(value)) return textIn(value);
  if (type === "markdown") return markdownOf(value);
  const markdown = textIn(value.markdown);
  return textIn(value.html) || (markdown && markdownToHtml(markdown));
}
