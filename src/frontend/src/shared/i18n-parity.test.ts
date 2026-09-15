import { describe, expect, it } from "vitest";
import en from "../i18n/en.json";
import es from "../i18n/es.json";
import ru from "../i18n/ru.json";

// There was no parity test, and the translator falls back through English to the
// key. Those two facts together mean a missing Russian string ships looking fine —
// an English sentence in the middle of a Russian interface, shown to the person
// least able to report it, with nothing failing anywhere.
//
// Projects added twenty-five keys at once, the largest batch the portal has taken.
describe("translation catalogues", () => {
  const catalogues: Record<string, Record<string, string>> = { es, ru };
  const english = en as Record<string, string>;

  for (const [code, catalogue] of Object.entries(catalogues)) {
    it(`${code} has every key English has`, () => {
      const missing = Object.keys(english).filter((key) => !(key in catalogue));
      expect(missing, `${code}.json is missing: ${missing.join(", ")}`).toEqual([]);
    });

    it(`${code} has no keys English does not`, () => {
      // A key nothing reads is a string nobody maintains, and it is usually the
      // fossil of a rename that only got halfway.
      const extra = Object.keys(catalogue).filter((key) => !(key in english));
      expect(extra, `${code}.json has stale keys: ${extra.join(", ")}`).toEqual([]);
    });

    it(`${code} keeps the same interpolation placeholders`, () => {
      // {name} translated as {nombre} renders the literal braces to the reader.
      // Silent, and only in that language.
      const wrong: string[] = [];
      for (const [key, template] of Object.entries(english)) {
        const expected = [...template.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();
        const actual = [...(catalogue[key] ?? "").matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();
        if (expected.join(",") !== actual.join(",")) {
          wrong.push(`${key}: expected {${expected.join("}, {")}} got {${actual.join("}, {")}}`);
        }
      }
      expect(wrong, wrong.join("\n")).toEqual([]);
    });
  }
});
