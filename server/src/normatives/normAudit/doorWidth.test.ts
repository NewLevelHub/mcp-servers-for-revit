import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  classifyDoorWidths,
  isDoorAccessory,
  type DoorWidthInput,
} from "./doorWidth.js";

describe("isDoorAccessory", () => {
  it("flags откос / наличник door accessories (REV-41)", () => {
    assert.equal(isDoorAccessory("(откос)двери_внутренний", "тип1"), true);
    assert.equal(isDoorAccessory("Дверь_наличник", ""), true);
    assert.equal(isDoorAccessory("Door", "jamb trim"), true);
  });

  it("does not flag real door blocks", () => {
    assert.equal(isDoorAccessory("Дверь однопольная", "900x2100"), false);
    assert.equal(isDoorAccessory("", ""), false);
  });
});

describe("classifyDoorWidths (golden fixtures)", () => {
  const doors: DoorWidthInput[] = [
    // ok egress door
    {
      id: 1,
      family: "Дверь",
      type: "900",
      openingWidthMm: 900,
      isOnEgressPath: true,
    },
    // narrow egress door → violation
    {
      id: 2,
      family: "Дверь",
      type: "700",
      openingWidthMm: 700,
      isOnEgressPath: true,
    },
    // borderline egress door within tolerance → nearLimit
    {
      id: 3,
      family: "Дверь",
      type: "870",
      openingWidthMm: 870,
      isOnEgressPath: true,
    },
    // откос — must NOT count as a door at all (REV-41)
    {
      id: 4,
      family: "(откос)двери_внутренний",
      type: "x",
      openingWidthMm: 200,
      isOnEgressPath: true,
    },
    // interior non-egress door 700 mm — must NOT be flagged (no applicable norm)
    {
      id: 5,
      family: "Дверь",
      type: "700",
      openingWidthMm: 700,
      isOnEgressPath: false,
    },
  ];

  it("flags only narrow egress doors, keeps ok as compliant", () => {
    const result = classifyDoorWidths(doors, {
      minWidthMm: 900,
      nearLimitToleranceMm: 50,
    });

    assert.equal(result.violations.length, 1);
    assert.equal(result.violations[0].id, 2);
    assert.equal(result.violations[0].actualMm, 700);
    assert.equal(result.violations[0].deviationMm, 200);

    assert.equal(result.nearLimit.length, 1);
    assert.equal(result.nearLimit[0].id, 3);

    assert.equal(result.compliant.length, 1);
    assert.equal(result.compliant[0].id, 1);
  });

  it("excludes откосы and does not flag non-egress interior doors", () => {
    const result = classifyDoorWidths(doors, { minWidthMm: 900 });

    // door id 4 is an accessory → excluded from door count entirely
    assert.equal(result.accessoriesSkipped, 1);
    assert.equal(result.totalDoors, 4);
    assert.ok(
      ![...result.violations, ...result.nearLimit, ...result.compliant].some(
        (d) => d.id === 4
      )
    );

    // door id 5 (non-egress, 700 mm) must not be a violation
    assert.equal(result.nonEgressSkipped, 1);
    assert.ok(!result.violations.some((d) => d.id === 5));
  });

  it("can check every door when egressOnly=false", () => {
    const result = classifyDoorWidths(doors, {
      minWidthMm: 900,
      egressOnly: false,
    });
    // now the non-egress 700 mm door (id 5) is also a violation
    assert.ok(result.violations.some((d) => d.id === 5));
    assert.equal(result.nonEgressSkipped, 0);
  });
});
