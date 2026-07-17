import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  classifyRamps,
  classifyRailingHeights,
  classifyStairRiserTreads,
  classifyStairWidths,
} from "./verticalCirculation.js";

describe("classifyStairWidths (golden fixtures)", () => {
  it("flags narrow march, keeps ok compliant", () => {
    const result = classifyStairWidths(
      [
        { id: 1, name: "Л1", widthMm: 1200 },
        { id: 2, name: "узкая", widthMm: 800 },
        { id: 3, name: "грань", widthMm: 1000 },
      ],
      { minWidthMm: 1050, nearLimitToleranceMm: 50 }
    );
    assert.equal(result.violations.length, 1);
    assert.equal(result.violations[0].id, 2);
    assert.equal(result.nearLimit.length, 1);
    assert.equal(result.nearLimit[0].id, 3);
    assert.equal(result.compliant.length, 1);
    assert.equal(result.compliant[0].id, 1);
  });
});

describe("classifyRamps (golden fixtures)", () => {
  it("flags steep ramp slope and narrow width", () => {
    const result = classifyRamps(
      [
        { id: 10, name: "ок", widthMm: 1200, slopePercent: 4 },
        { id: 11, name: "крутой", widthMm: 1200, slopePercent: 12 },
        { id: 12, name: "узкий", widthMm: 900, slopePercent: 5 },
      ],
      { minWidthMm: 1200, maxSlopePercent: 5, nearLimitTolerancePercent: 0.5 }
    );
    assert.ok(result.violations.some((v) => v.id === 11 && v.metric === "уклон"));
    assert.ok(result.violations.some((v) => v.id === 12 && v.metric === "ширина"));
    assert.ok(result.compliant.some((c) => c.id === 10 && c.metric === "уклон"));
  });
});

describe("classifyStairRiserTreads + railing (golden)", () => {
  it("flags tall riser and low railing", () => {
    const steps = classifyStairRiserTreads(
      [
        { id: 1, riserMm: 170, treadMm: 280 },
        { id: 2, riserMm: 210, treadMm: 280 },
      ],
      { maxRiserMm: 190, minTreadMm: 250 }
    );
    assert.equal(steps.violations.length, 1);
    assert.equal(steps.violations[0].metric, "подступенок");

    const rails = classifyRailingHeights(
      [
        { id: 5, heightMm: 1200 },
        { id: 6, heightMm: 900 },
        {
          id: 7,
          name: "ADSK_Стандартное_h 1200 Поручень",
          type: "ADSK_Стандартное_h 1200",
          heightMm: null,
        },
        {
          id: 8,
          name: "ADSK_МГН_h 900",
          type: "ADSK_МГН_h 900",
          heightMm: null,
        },
      ],
      { minHeightMm: 1150 }
    );
    assert.equal(rails.violations.length, 1);
    assert.equal(rails.violations[0].id, 6);
    assert.ok(rails.compliant.some((c) => c.id === 7 && c.actualMm === 1200));
    assert.equal(rails.skippedHandrails, 1);
  });
});
