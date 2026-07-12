import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  extractMinDimensionRulesFromText,
  inferMinDimensionRule,
  inferMinDimensionRules,
  pickPrimaryMinDimensionRules,
  resolveMinDimensionLimits,
} from "./minDimensionsRules.js";

describe("minDimensionsRules", () => {
  it("extracts balcony/loggia width from SP RK 3.06-101", () => {
    const rules = extractMinDimensionRulesFromText(
      "4.3.2.40 Ширина балконов и лоджий должна быть, как правило, не менее 1,4 м в свету.",
      "СП РК 3.06-101-2012",
      "SP_RK_3.06-101-2012_27.11.2019.pdf"
    );
    assert.ok(rules.length >= 1);
    const widthRule = rules.find((r) => r.metric === "width");
    assert.ok(widthRule);
    assert.equal(widthRule!.minValueMm, 1400);
  });

  it("extracts fire pier rules from SP RK 3.02-101", () => {
    const rules = extractMinDimensionRulesFromText(
      "простенком не менее 1,2 м от торца балкона до оконного проема. " +
        "не менее 1,6 м между остекленными проемами, выходящими на балкон (лоджию).",
      "СП РК 3.02-101-2012",
      "SP_RK_3.02-101-2012_27.04.2021.pdf"
    );
    const pierTo = rules.find((r) => r.metric === "pier_to_opening");
    const pierBetween = rules.find((r) => r.metric === "pier_between_openings");
    assert.ok(pierTo);
    assert.ok(pierBetween);
    assert.equal(pierTo!.minValueMm, 1200);
    assert.equal(pierBetween!.minValueMm, 1600);
  });

  it("extracts loggia depth from SP RK 3.06-31", () => {
    const rule = inferMinDimensionRule(
      "5.14 лоджия должна быть остекленной и иметь глубину не менее 1,6 м.",
      "СП РК 3.06-31-2005",
      "SP_RK_3.06-31-2005.pdf"
    );
    assert.ok(rule);
    assert.equal(rule!.metric, "depth");
    assert.equal(rule!.minValueMm, 1600);
  });

  it("pickPrimaryMinDimensionRules prefers SP RK clauses", () => {
    const rules = [
      inferMinDimensionRule(
        "Ширина балконов должна быть не менее 1,2 м.",
        "документ A",
        "a.pdf"
      )!,
      ...inferMinDimensionRules(
        "4.3.2.40 Ширина балконов и лоджий должна быть, как правило, не менее 1,4 м в свету.",
        "СП РК 3.06-101-2012",
        "SP_RK_3.06-101-2012_27.11.2019.pdf"
      ),
    ].filter(Boolean);

    const primary = pickPrimaryMinDimensionRules(rules);
    assert.equal(primary["балкон:width"]?.minValueMm, 1400);
    assert.equal(primary["лоджия:width"]?.minValueMm, 1400);
  });

  it("resolveMinDimensionLimits merges overrides", () => {
    const rules = extractMinDimensionRulesFromText(
      "Ширина балконов и лоджий должна быть не менее 1,4 м в свету. " +
        "лоджия должна иметь глубину не менее 1,6 м. " +
        "простенком не менее 1,2 м от торца балкона до оконного проема. " +
        "не менее 1,6 м между остекленными проемами.",
      "СП РК 3.02-101-2012",
      "test.pdf"
    );
    const limits = resolveMinDimensionLimits(rules, { minBalconyWidthMm: 1500 });
    assert.equal(limits.minBalconyWidthMm, 1500);
    assert.ok(limits.minLoggiaWidthMm === undefined || limits.minLoggiaWidthMm >= 1400);
  });
});
