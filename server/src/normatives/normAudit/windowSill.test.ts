import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  classifyWindowSills,
  isWindowAccessory,
  type WindowSillInput,
} from "./windowSill.js";

describe("isWindowAccessory", () => {
  it("flags sill / откос accessories", () => {
    assert.equal(isWindowAccessory("подоконник_ПВХ", "тип 1"), true);
    assert.equal(isWindowAccessory("(откос)окна", ""), true);
    assert.equal(isWindowAccessory("Window", "window sill"), true);
  });

  it("does not flag real window blocks", () => {
    assert.equal(isWindowAccessory("Окно ПВХ", "1200x1400"), false);
  });
});

describe("classifyWindowSills (golden fixtures)", () => {
  const windows: WindowSillInput[] = [
    // ok sill ≥ 900
    {
      id: 1,
      category: "window",
      family: "Окно",
      type: "1200",
      sillHeightMm: 900,
    },
    // too low → violation
    {
      id: 2,
      category: "window",
      family: "Окно",
      type: "низкий",
      sillHeightMm: 600,
    },
    // borderline within 50 mm → nearLimit
    {
      id: 3,
      category: "window",
      family: "Окно",
      type: "860",
      sillHeightMm: 860,
    },
    // accessory — excluded
    {
      id: 4,
      category: "window",
      family: "подоконник_ПВХ",
      type: "x",
      sillHeightMm: 100,
    },
    // door row — skipped for sill check
    {
      id: 5,
      category: "door",
      family: "Дверь",
      type: "900",
      sillHeightMm: null,
    },
  ];

  it("flags only low window sills; keeps ok compliant", () => {
    const result = classifyWindowSills(windows, {
      minSillHeightMm: 900,
      nearLimitToleranceMm: 50,
    });

    assert.equal(result.violations.length, 1);
    assert.equal(result.violations[0].id, 2);
    assert.equal(result.violations[0].actualMm, 600);
    assert.equal(result.violations[0].deviationMm, 300);

    assert.equal(result.nearLimit.length, 1);
    assert.equal(result.nearLimit[0].id, 3);

    assert.equal(result.compliant.length, 1);
    assert.equal(result.compliant[0].id, 1);
  });

  it("excludes accessories and non-windows", () => {
    const result = classifyWindowSills(windows, { minSillHeightMm: 900 });

    assert.equal(result.accessoriesSkipped, 1);
    assert.equal(result.nonWindowsSkipped, 1);
    assert.equal(result.totalWindows, 3);
    assert.ok(
      ![...result.violations, ...result.nearLimit, ...result.compliant].some(
        (w) => w.id === 4 || w.id === 5
      )
    );
  });
});
