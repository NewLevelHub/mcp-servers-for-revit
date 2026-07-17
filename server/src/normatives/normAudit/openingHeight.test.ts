import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  classifyOpeningHeights,
  type OpeningHeightInput,
} from "./openingHeight.js";

describe("classifyOpeningHeights (golden fixtures)", () => {
  const openings: OpeningHeightInput[] = [
    // ok egress door
    {
      id: 1,
      category: "door",
      family: "Дверь",
      type: "2100",
      openingHeightMm: 2100,
      isOnEgressPath: true,
    },
    // short egress door → violation
    {
      id: 2,
      category: "door",
      family: "Дверь",
      type: "1800",
      openingHeightMm: 1800,
      isOnEgressPath: true,
    },
    // borderline within 50 mm → nearLimit
    {
      id: 3,
      category: "door",
      family: "Дверь",
      type: "1860",
      openingHeightMm: 1860,
      isOnEgressPath: true,
    },
    // откос — excluded
    {
      id: 4,
      category: "door",
      family: "(откос)двери_внутренний",
      type: "x",
      openingHeightMm: 500,
      isOnEgressPath: true,
    },
    // interior non-egress — skipped by default
    {
      id: 5,
      category: "door",
      family: "Дверь",
      type: "2000",
      openingHeightMm: 1700,
      isOnEgressPath: false,
    },
    // window — skipped when egressDoorsOnly (typical 1.9 m egress rule)
    {
      id: 6,
      category: "window",
      family: "Окно",
      type: "1400",
      openingHeightMm: 1400,
      isOnEgressPath: false,
    },
  ];

  it("flags only short egress doors; keeps ok compliant", () => {
    const result = classifyOpeningHeights(openings, {
      minHeightMm: 1900,
      nearLimitToleranceMm: 50,
    });

    assert.equal(result.violations.length, 1);
    assert.equal(result.violations[0].id, 2);
    assert.equal(result.violations[0].actualMm, 1800);
    assert.equal(result.violations[0].deviationMm, 100);

    assert.equal(result.nearLimit.length, 1);
    assert.equal(result.nearLimit[0].id, 3);

    assert.equal(result.compliant.length, 1);
    assert.equal(result.compliant[0].id, 1);
  });

  it("excludes откосы, non-egress doors, and windows by default", () => {
    const result = classifyOpeningHeights(openings, { minHeightMm: 1900 });

    assert.equal(result.accessoriesSkipped, 1);
    assert.equal(result.nonEgressSkipped, 1);
    assert.equal(result.windowsSkipped, 1);
    assert.ok(
      ![...result.violations, ...result.nearLimit, ...result.compliant].some(
        (o) => o.id === 4 || o.id === 5 || o.id === 6
      )
    );
  });
});
