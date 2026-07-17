import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  extractMinDimensionRulesFromText,
  inferMinDimensionRule,
  inferMinDimensionRules,
  inferWidthMeasurementBasis,
  isMgnOrSpecialHousingRule,
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
    assert.equal(isMgnOrSpecialHousingRule(widthRule!), true);
    assert.equal(inferWidthMeasurementBasis(widthRule), "clear_width");
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
    assert.equal(isMgnOrSpecialHousingRule(rule!), true);
  });

  it("ordinary housing: no private balcony width; 1.2 m only as fire-path (REV-50)", () => {
    const rules = [
      ...inferMinDimensionRules(
        "4.6.5 В квартирах для престарелых и семей с инвалидами ширина балконов и лоджий должна быть не менее 1,4 м (от наружной стены до ограждения балкона).",
        "СП РК 3.02-101-2012",
        "SP_RK_3.02-101-2012_27.04.2021.pdf"
      ),
      ...inferMinDimensionRules(
        "4.3.2.40 Ширина балконов и лоджий должна быть, как правило, не менее 1,4 м в свету.",
        "СП РК 3.06-101-2012",
        "SP_RK_3.06-101-2012_27.11.2019.pdf"
      ),
      ...inferMinDimensionRules(
        "4.2.30 Балконы и лоджии или галереи, ведущие к незадымляемой лестничной клетке 1-го типа, должны иметь ширину не менее 1,2 м с высотой ограждения не менее 1,2 м.",
        "СП РК 3.02-101-2012",
        "SP_RK_3.02-101-2012_27.04.2021.pdf"
      ),
      ...extractMinDimensionRulesFromText(
        "простенком не менее 1,2 м от торца балкона до оконного проема. " +
          "не менее 1,6 м между остекленными проемами, выходящими на балкон (лоджию).",
        "СП РК 3.02-101-2012",
        "SP_RK_3.02-101-2012_27.04.2021.pdf"
      ),
    ];

    const ordinary = resolveMinDimensionLimits(rules, { housingType: "ordinary" });
    assert.equal(ordinary.minBalconyWidthMm, undefined);
    assert.equal(ordinary.minLoggiaWidthMm, undefined);
    assert.equal(ordinary.minLoggiaDepthMm, undefined);
    assert.equal(ordinary.minFirePathOutdoorWidthMm, 1200);
    assert.ok(ordinary.skippedMgnRules >= 1);
    assert.equal(ordinary.minFirePierToOpeningMm, 1200);
    assert.equal(ordinary.minFirePierBetweenOpeningsMm, 1600);
    assert.equal(ordinary.housingType, "ordinary");

    const mgn = resolveMinDimensionLimits(rules, { housingType: "mgn" });
    assert.equal(mgn.minBalconyWidthMm, 1400);
    assert.equal(mgn.minLoggiaWidthMm, 1400);
  });

  it("pickPrimary for mgn still prefers 1.4 m", () => {
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

    const primary = pickPrimaryMinDimensionRules(rules, { housingType: "mgn" });
    assert.equal(primary["балкон:width"]?.minValueMm, 1400);
    assert.equal(primary["лоджия:width"]?.minValueMm, 1400);
  });

  it("resolveMinDimensionLimits merges overrides on ordinary housing", () => {
    const rules = extractMinDimensionRulesFromText(
      "простенком не менее 1,2 м от торца балкона до оконного проема. " +
        "не менее 1,6 м между остекленными проемами.",
      "СП РК 3.02-101-2012",
      "test.pdf"
    );
    const limits = resolveMinDimensionLimits(rules, {
      housingType: "ordinary",
      minBalconyWidthMm: 1500,
    });
    assert.equal(limits.minBalconyWidthMm, 1500);
    assert.equal(limits.minFirePierToOpeningMm, 1200);
    assert.equal(limits.minFirePierBetweenOpeningsMm, 1600);
  });

  it("infers wall_to_rail measurement basis for п. 4.6.5", () => {
    const rules = inferMinDimensionRules(
      "4.6.5 В квартирах для престарелых ширина балконов и лоджий должна быть не менее 1,4 м (от наружной стены до ограждения балкона).",
      "СП РК 3.02-101-2012",
      "SP_RK_3.02-101-2012_27.04.2021.pdf"
    );
    const width = rules.find((r) => r.metric === "width");
    assert.ok(width);
    assert.equal(inferWidthMeasurementBasis(width), "wall_to_rail");
  });
});
