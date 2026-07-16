import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  DEFAULT_FLOOR_THICKNESS_MM,
  isLikelyDefaultRoomHeight,
  resolveRoomClearHeight,
} from "./resolveRoomClearHeight.js";

const levels = [
  { levelName: "1 этаж", elevationMm: 0, storeyKind: "aboveGround" },
  { levelName: "2 этаж", elevationMm: 3900, storeyKind: "aboveGround" },
  { levelName: "3 этаж", elevationMm: 6900, storeyKind: "aboveGround" },
];

describe("isLikelyDefaultRoomHeight", () => {
  it("detects Revit 8'-0\" default", () => {
    assert.equal(isLikelyDefaultRoomHeight(2438.4), true);
    assert.equal(isLikelyDefaultRoomHeight(2438), true);
    assert.equal(isLikelyDefaultRoomHeight(2700), false);
  });
});

describe("resolveRoomClearHeight", () => {
  it("ignores 8ft room default and uses storey − slab", () => {
    const resolved = resolveRoomClearHeight(
      {
        levelName: "2 этаж",
        unboundedHeightMm: 2438.4,
      },
      levels
    );
    assert.equal(resolved.source, "level_clear");
    assert.equal(resolved.storeyHeightMm, 3000);
    assert.equal(resolved.heightMm, 3000 - DEFAULT_FLOOR_THICKNESS_MM);
  });

  it("uses exported clear height when provided", () => {
    const resolved = resolveRoomClearHeight(
      {
        levelName: "2 этаж",
        unboundedHeightMm: 2438.4,
        exportedClearHeightMm: 2700,
        exportedStoreyHeightMm: 3000,
      },
      levels
    );
    assert.equal(resolved.heightMm, 2700);
    assert.equal(resolved.source, "level_clear");
  });

  it("trusts room height when it matches clear estimate", () => {
    const resolved = resolveRoomClearHeight(
      {
        levelName: "2 этаж",
        unboundedHeightMm: 2700,
      },
      levels
    );
    assert.equal(resolved.heightMm, 2700);
    assert.equal(resolved.source, "room_unbounded");
  });

  it("matches this project: 3000 storey → 2700 clear ≥ 2500", () => {
    const resolved = resolveRoomClearHeight(
      { levelName: "11 этаж", unboundedHeightMm: 2438.4 },
      [
        { levelName: "11 этаж", elevationMm: 30900, storeyKind: "aboveGround" },
        { levelName: "12 этаж", elevationMm: 33900, storeyKind: "aboveGround" },
      ]
    );
    assert.equal(resolved.heightMm, 2700);
    assert.ok((resolved.heightMm ?? 0) >= 2500);
  });
});
