import { describe, expect, it } from "vitest";
import { ref } from "vue";
import { mount } from "@vue/test-utils";
import Fields from "../../src/modules/designer/components/Fields.vue";
import { PaletteTokensKey, type Field } from "../../src/modules/designer/types";

const TOKEN: Field = { id: "background", type: "color-token", label: "Background" };

const mountToken = (value: unknown) =>
  mount(Fields, {
    props: { fields: [TOKEN], values: { background: value }, context: {} },
    global: { provide: { [PaletteTokensKey as symbol]: ref({ tokens: ["primary"], colors: { primary: "#000000" } }) } },
  });

const optionTexts = (value: unknown) =>
  mountToken(value)
    .findAll("option")
    .slice(1)
    .map((x) => x.text());

describe("a color token field", () => {
  it("lists the palette's tokens", () => {
    expect(optionTexts("primary")).toEqual(["Primary"]);
  });

  it("keeps a token the palette does not have, marked as unknown", () => {
    expect(optionTexts("retired")).toEqual(["retired · not in the storefront's palette", "Primary"]);
  });

  it("selects the token of the value", () => {
    expect((mountToken("primary").find("select").element as HTMLSelectElement).value).toBe("0");
  });
});
