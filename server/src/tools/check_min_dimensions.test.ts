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

  it("wraps norm limits in response shape", () => {
    const payload = {
      norm: {
        limits: {
          minBalconyWidthMm: 1400,
          minFirePierToOpeningMm: 1200,
          appliedRules: [
            {
              object: "балкон",
              metric: "width",
              minValueMm: 1400,
              source: {
                document: "СП РК 3.06-101-2012",
                clause: "п. 4.3.2.40",
                quote: "Ширина балконов и лоджий должна быть не менее 1,4 м в свету.",
              },
            },
          ],
        },
      },
      success: true,
      violationCount: 1,
      violations: [{ id: 42, metric: "width", actualValueMm: 1300, deviationMm: 100 }],
    };

    assert.equal(payload.norm.limits.minBalconyWidthMm, 1400);
    assert.equal(payload.norm.limits.minFirePierToOpeningMm, 1200);
    assert.equal(payload.violations[0].id, 42);
  });
});
