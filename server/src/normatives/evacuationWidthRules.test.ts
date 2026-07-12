import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  extractEvacuationWidthRulesFromText,
  pickPrimaryEvacuationWidthRule,
} from "./evacuationWidthRules.js";

describe("evacuationWidthRules", () => {
  it("extracts evacuation corridor width in millimeters", () => {
    const rules = extractEvacuationWidthRulesFromText(
      "6.3.1 Ширина эвакуационного коридора должна быть не менее 1200 мм для зданий класса Ф1.1.",
      "СП РК 3.02-101-2012",
      "SP_RK_3.02-101-2012_27.04.2021.pdf"
    );

    assert.equal(rules.length, 1);
    assert.equal(rules[0].object, "эвакуационный коридор");
    assert.equal(rules[0].minWidthMm, 1200);
    assert.match(rules[0].source.quote, /1200/);
  });

  it("extracts residential corridor width in meters", () => {
    const rules = extractEvacuationWidthRulesFromText(
      "4.2.3 Ширина коридора в жилых зданиях должна быть не менее 1,2 м при двустороннем расположении дверей.",
      "СП РК 3.02-101-2012",
      "SP_RK_3.02-101-2012_27.04.2021.pdf"
    );

    assert.equal(rules.length, 1);
    assert.equal(rules[0].object, "коридор");
    assert.equal(rules[0].minWidthMm, 1200);
  });

  it("prefers evacuation corridor rule over generic corridor", () => {
    const rules = extractEvacuationWidthRulesFromText(
      "4.2.3 Ширина коридора в жилых зданиях должна быть не менее 1,2 м. " +
        "6.3.1 Ширина эвакуационного коридора должна быть не менее 1200 мм для зданий класса Ф1.1.",
      "СП РК 3.02-101-2012",
      "SP_RK_3.02-101-2012_27.04.2021.pdf"
    );

    const primary = pickPrimaryEvacuationWidthRule(rules);
    assert.ok(primary);
    assert.equal(primary.object, "эвакуационный коридор");
    assert.equal(primary.minWidthMm, 1200);
  });

  it("filters by building class when provided", () => {
    const rules = extractEvacuationWidthRulesFromText(
      "4.2.3 Ширина коридора в жилых зданиях должна быть не менее 1,5 м. " +
        "6.3.1 Ширина эвакуационного коридора должна быть не менее 1200 мм для зданий класса Ф1.1.",
      "СП РК 3.02-101-2012",
      "SP_RK_3.02-101-2012_27.04.2021.pdf"
    );

    const primary = pickPrimaryEvacuationWidthRule(rules, { buildingClass: "ф1.1" });
    assert.ok(primary);
    assert.match(primary.source.quote, /ф1\.1/i);
  });

  it("ignores ventilation table noise with м³/ч", () => {
    const rules = extractEvacuationWidthRulesFromText(
      "10 Вестибюль, общий коридор, лестничная клетка 18 - - Не менее 60 м 3 /ч при 2-конфорочных плитах.",
      "СП РК 3.02-101-2012",
      "SP_RK_3.02-101-2012_27.04.2021.pdf"
    );

    assert.equal(rules.length, 0);
  });
});
