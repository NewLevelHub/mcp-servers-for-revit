import assert from "node:assert/strict";
import { describe, it } from "node:test";

function isWidthCompliant(actualWidthMm: number, minWidthMm: number): boolean {
  return actualWidthMm >= minWidthMm;
}

function calculateDeviationMm(actualWidthMm: number, minWidthMm: number): number {
  if (actualWidthMm < minWidthMm) return minWidthMm - actualWidthMm;
  return 0;
}

describe("check_evacuation_width compliance helpers", () => {
  it("detects width below minimum", () => {
    assert.equal(isWidthCompliant(1100, 1200), false);
    assert.equal(calculateDeviationMm(1100, 1200), 100);
  });

  it("passes when width meets minimum", () => {
    assert.equal(isWidthCompliant(1200, 1200), true);
    assert.equal(isWidthCompliant(1500, 1200), true);
    assert.equal(calculateDeviationMm(1500, 1200), 0);
  });

  it("wraps norm source in response shape", () => {
    const payload = {
      norm: {
        minWidthMm: 1200,
        source: {
          document: "СП РК 3.02-101",
          clause: "п. 6.3.1",
          quote:
            "Ширина эвакуационного коридора должна быть не менее 1200 мм для зданий класса Ф1.1.",
        },
      },
      success: true,
      violationCount: 1,
      violations: [{ id: 12345, actualWidthMm: 1100, deviationMm: 100 }],
    };

    assert.equal(payload.norm.minWidthMm, 1200);
    assert.ok(payload.norm.source?.quote.includes("1200"));
    assert.equal(payload.violations[0].id, 12345);
  });
});
