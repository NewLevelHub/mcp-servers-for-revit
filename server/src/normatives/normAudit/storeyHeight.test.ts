import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { classifyStoreyHeights, computeStoreyHeights } from "./storeyHeight.js";

describe("computeStoreyHeights", () => {
  it("computes deltas for above-ground levels only", () => {
    const pairs = computeStoreyHeights([
      { levelName: "Подвал", elevationMm: 0, storeyKind: "basement" },
      { levelName: "1 этаж", elevationMm: 0, storeyKind: "aboveGround" },
      { levelName: "2 этаж", elevationMm: 3000, storeyKind: "aboveGround" },
    ]);
    assert.equal(pairs.length, 1);
    assert.equal(pairs[0].heightMm, 3000);
  });
});

describe("classifyStoreyHeights", () => {
  it("flags short storey", () => {
    const result = classifyStoreyHeights(
      [
        { levelName: "1 этаж", elevationMm: 0, storeyKind: "aboveGround" },
        { levelName: "2 этаж", elevationMm: 2700, storeyKind: "aboveGround" },
      ],
      { minStoreyHeightMm: 2800 }
    );
    assert.equal(result.violations.length, 1);
    assert.equal(result.violations[0].actualHeightMm, 2700);
  });
});
