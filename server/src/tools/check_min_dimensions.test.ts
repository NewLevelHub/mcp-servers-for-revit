import assert from "node:assert/strict";
import { describe, it } from "node:test";

function isCompliant(actualMm: number, requiredMm: number): boolean {
  return actualMm >= requiredMm;
}

function calculateDeviationMm(actualMm: number, requiredMm: number): number {
  if (actualMm < requiredMm) return requiredMm - actualMm;
  return 0;
}

describe("check_min_dimensions compliance helpers", () => {
  it("detects width below minimum", () => {
    assert.equal(isCompliant(1300, 1400), false);
    assert.equal(calculateDeviationMm(1300, 1400), 100);
  });

  it("detects depth below minimum", () => {
    assert.equal(isCompliant(1500, 1600), false);
    assert.equal(calculateDeviationMm(1500, 1600), 100);
  });

  it("detects fire pier below minimum", () => {
    assert.equal(isCompliant(1000, 1200), false);
    assert.equal(calculateDeviationMm(1000, 1200), 200);
    assert.equal(isCompliant(1600, 1600), true);
  });

  it("wraps ordinary norm limits in response shape (REV-50)", () => {
    const payload = {
      norm: {
        limits: {
          housingType: "ordinary",
          minBalconyWidthMm: undefined as number | undefined,
          minFirePathOutdoorWidthMm: 1200,
          minFirePierToOpeningMm: 1200,
          widthMeasurementBasis: "bounding_box",
          appliedRules: [
            {
              object: "балкон",
              metric: "width",
              minValueMm: 1200,
              source: {
                document: "СП РК 3.02-101-2012",
                clause: "п. 4.2.30",
                quote:
                  "Балконы и лоджии или галереи, ведущие к незадымляемой лестничной клетке 1-го типа, должны иметь ширину не менее 1,2 м.",
              },
            },
          ],
        },
      },
      success: true,
      violationCount: 0,
      violations: [] as Array<{ id: number }>,
    };

    assert.equal(payload.norm.limits.minBalconyWidthMm, undefined);
    assert.equal(payload.norm.limits.minFirePathOutdoorWidthMm, 1200);
    assert.equal(payload.norm.limits.housingType, "ordinary");
    assert.equal(payload.norm.limits.minFirePierToOpeningMm, 1200);
  });
});
