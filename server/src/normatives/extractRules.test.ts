import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  extractRulesFromText,
  NORMATIVE_SAMPLE_TEXTS,
} from "./extractRules.js";

describe("extractRulesFromText", () => {
  it("extracts GOST 21.101-97 sample rules with mm units", () => {
    const result = extractRulesFromText({
      text: NORMATIVE_SAMPLE_TEXTS.gost2110197.text,
      document: NORMATIVE_SAMPLE_TEXTS.gost2110197.document,
    });

    assert.equal(result.metadata.mode, "embedded-text");
    assert.ok(result.rules.length >= 2);

    const lineHeight = result.rules.find((rule) =>
      rule.source.quote.includes("высотой строки")
    );
    assert.ok(lineHeight);
    assert.equal(lineHeight?.type, "min_value");
    assert.equal(lineHeight?.unit, "mm");
    assert.equal(lineHeight?.normalized?.exact, 5);
    assert.match(lineHeight?.source.clause ?? "", /5\.1\.4/);
  });

  it("extracts SP RK 3.02-101 corridor and area rules", () => {
    const result = extractRulesFromText({
      text: NORMATIVE_SAMPLE_TEXTS.spRk302101.text,
      document: NORMATIVE_SAMPLE_TEXTS.spRk302101.document,
    });

    const corridor = result.rules.find((rule) => rule.object === "коридор");
    assert.ok(corridor);
    assert.equal(corridor?.type, "min_value");
    assert.equal(corridor?.unit, "m");
    assert.equal(corridor?.normalized?.exact, 1200);
    assert.ok(corridor?.applicability?.raw.toLowerCase().includes("жил"));

    const area = result.rules.find(
      (rule) => rule.object === "жилое помещение" || rule.object === "площадь"
    );
    assert.ok(area);
    assert.equal(area?.unit, "m2");
    assert.equal(area?.value, 9);

    const evacuation = result.rules.find(
      (rule) => rule.object === "эвакуационный коридор"
    );
    assert.ok(evacuation);
    assert.equal(evacuation?.unit, "mm");
    assert.equal(evacuation?.normalized?.exact, 1200);
    assert.ok(evacuation?.applicability?.conditions?.[0]?.includes("Ф1.1"));
  });

  it("preserves OCR metadata without changing rule schema", () => {
    const result = extractRulesFromText({
      text: "4.1.1 Ширина двери должна быть не менее 900 мм.",
      document: "СП РК 3.02-101",
      metadata: {
        mode: "ocr",
        confidence: 0.82,
        extractedAt: "2026-07-09T10:00:00.000Z",
      },
    });

    assert.equal(result.metadata.mode, "ocr");
    assert.equal(result.metadata.confidence, 0.82);
    assert.equal(result.rules[0]?.unit, "mm");
    assert.equal(result.rules[0]?.normalized?.exact, 900);
  });

  it("extracts kazakh minimum-width rule from SP RK text", () => {
    const result = extractRulesFromText({
      text: "4.2.3 Тұрғын ғимараттардағы дәліз ені кемінде 1,2 м болуы тиіс.",
      document: "СП РК 3.02-101",
    });

    assert.equal(result.rules.length, 1);
    assert.equal(result.rules[0]?.object, "коридор");
    assert.equal(result.rules[0]?.type, "min_value");
    assert.equal(result.rules[0]?.unit, "m");
    assert.equal(result.rules[0]?.normalized?.exact, 1200);
    assert.ok(result.rules[0]?.applicability?.raw.toLowerCase().includes("тұрғын"));
  });
});
