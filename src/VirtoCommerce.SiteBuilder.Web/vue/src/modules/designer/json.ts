export const isRecord = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);

export const isText = (value: unknown): value is string => typeof value === "string" && !!value.trim();

/** The order a bare sort() gives: ids and file keys keep it, whatever the browser's locale. */
export const byCodeUnit = (a: string, b: string) => Number(a > b) - Number(a < b);

export const isTextList = (value: unknown): value is string[] => Array.isArray(value) && value.every((x) => typeof x === "string");

/** structuredClone refuses reactive proxies. */
export const clone = <T>(value: T): T => (value === undefined ? value : (JSON.parse(JSON.stringify(value)) as T));

/** VcTextarea's default maxlength is short: a rules file, a copied section or a markdown text is often longer. */
export const LONG_TEXT_MAX_LENGTH = "1000000";
