import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  classifyAccessibilityRooms,
  classifyAccessibilityRamps,
  classifyDoorManeuvering,
  isAccessibleSanitaryRoom,
  isCorridorRoom,
  isEvacuationCorridor,
  MGN_CORRIDOR_SOURCE,
  MGN_DOOR_SOURCE,
  MGN_DOOR_WIDTH_MM,
  MGN_RAMP_SLOPE_SOURCE,
  MGN_TURNING_DIAMETER_MM,
  MGN_TURNING_SOURCE,
  MGN_WC_SOURCE,
  requiredAccessibleWcDimsMm,
} from "./accessibility.js";
import { classifyDoorWidths } from "./doorWidth.js";
import { selectPhase1Checkers, selectSkippedRules } from "./checklist.js";
import { paintTargetsFromFindings } from "./runners.js";

describe("accessibility room matchers", () => {
  it("recognizes corridors, evacuation corridors, accessible WCs", () => {
    assert.equal(isCorridorRoom("Коридор МОП"), true);
    assert.equal(isCorridorRoom("Спальня"), false);
    assert.equal(isEvacuationCorridor("Эвакуационный коридор"), true);
    assert.equal(isEvacuationCorridor("Коридор"), false);
    assert.equal(isAccessibleSanitaryRoom("Санузел МГН"), true);
    assert.equal(isAccessibleSanitaryRoom("Универсальный санузел"), true);
    assert.equal(isAccessibleSanitaryRoom("Санузел"), false, "обычный санузел не флагуется");
    assert.equal(isAccessibleSanitaryRoom("Коридор МГН"), false, "не санитарное помещение");
  });

  it("resolves accessible WC dims per 4.3.3.14", () => {
    assert.deepEqual(requiredAccessibleWcDimsMm("Санузел МГН"), [2200, 2200]);
    assert.deepEqual(requiredAccessibleWcDimsMm("Ванная доступная"), [2200, 2200]);
    assert.deepEqual(requiredAccessibleWcDimsMm("Туалет МГН"), [1600, 2200]);
  });
});

describe("classifyAccessibilityRooms", () => {
  const rooms = [
    { id: 1, name: "Тамбур", level: "1 этаж", widthMm: 1400, depthMm: 2000 },
    { id: 2, name: "Тамбур входной", level: "1 этаж", widthMm: 1600, depthMm: 2300 },
    { id: 3, name: "Коридор МОП", level: "1 этаж", widthMm: 1450, depthMm: 12000 },
    { id: 4, name: "Эвакуационный коридор", level: "1 этаж", widthMm: 1600, depthMm: 20000 },
    { id: 5, name: "Санузел МГН", level: "1 этаж", widthMm: 2000, depthMm: 2100 },
    { id: 6, name: "Санузел", level: "1 этаж", widthMm: 1500, depthMm: 1700 },
    { id: 7, name: "Спальня", level: "1 этаж", widthMm: 3200, depthMm: 4000 },
  ];

  const result = classifyAccessibilityRooms(rooms, { nearLimitToleranceMm: 50 });

  it("checks turning circle 1500 in tambours and accessible WCs", () => {
    assert.equal(result.tamboursFound, 2);
    const narrowTambour = result.turning.find((room) => room.id === 1);
    assert.ok(narrowTambour);
    assert.equal(narrowTambour.status, "violation");
    assert.equal(narrowTambour.requiredMm, MGN_TURNING_DIAMETER_MM);
    assert.equal(narrowTambour.deviationMm, 100);

    const okTambour = result.turning.find((room) => room.id === 2);
    assert.equal(okTambour?.status, "compliant");
  });

  it("checks corridor width 1500 (evac 1800) by min side", () => {
    assert.equal(result.corridorsFound, 2);
    const corridor = result.corridors.find((room) => room.id === 3);
    assert.equal(corridor?.status, "nearLimit", "1450 vs 1500 = nearLimit при допуске 50");
    const evacCorridor = result.corridors.find((room) => room.id === 4);
    assert.equal(evacCorridor?.requiredMm, 1800);
    assert.equal(evacCorridor?.status, "violation", "1600 vs 1800");
  });

  it("checks accessible WC dims and ignores regular bathrooms", () => {
    assert.equal(result.accessibleWcFound, 1);
    const wc = result.wc.find((room) => room.id === 5);
    assert.equal(wc?.status, "violation", "2000×2100 vs 2200×2200");
    assert.equal(wc?.deviationMm, 200);
    assert.equal(
      result.wc.some((room) => room.id === 6),
      false,
      "обычный санузел не проверяется"
    );
  });
});

describe("МГН door width via classifyDoorWidths", () => {
  it("flags egress doors narrower than 900 mm", () => {
    const doors = [
      { id: 1, type: "Дверь 800", openingWidthMm: 800, isOnEgressPath: true },
      { id: 2, type: "Дверь 900", openingWidthMm: 900, isOnEgressPath: true },
      { id: 3, type: "Дверь СУ 600", openingWidthMm: 600, isOnEgressPath: false },
      { id: 4, family: "Откос", type: "Откос дверной", openingWidthMm: 500, isOnEgressPath: true },
    ];
    const classified = classifyDoorWidths(doors, {
      minWidthMm: MGN_DOOR_WIDTH_MM,
      nearLimitToleranceMm: 50,
      egressOnly: true,
    });
    assert.equal(classified.violations.length, 1);
    assert.equal(classified.violations[0].id, 1);
    assert.equal(classified.compliant.length, 1);
    assert.equal(classified.nonEgressSkipped, 1, "внутриквартирная дверь не флагуется");
    assert.equal(classified.accessoriesSkipped, 1);
  });
});

describe("norm sources carry verbatim СП РК 3.06-101-2012* quotes", () => {
  it("cites clause and numeric limits", () => {
    assert.equal(MGN_TURNING_SOURCE.document, "СП РК 3.06-101-2012*");
    assert.match(MGN_TURNING_SOURCE.clause, /4\.3\.2\.43/);
    assert.match(MGN_TURNING_SOURCE.quote, /не менее 1,5-1,7 м/);
    assert.match(MGN_CORRIDOR_SOURCE.quote, /не менее 1,5 м/);
    assert.match(MGN_CORRIDOR_SOURCE.quote, /не менее 1,8 м/);
    assert.match(MGN_DOOR_SOURCE.quote, /не менее 0,9 м/);
    assert.match(MGN_WC_SOURCE.quote, /2,2 × 2,2/);
    assert.match(MGN_RAMP_SLOPE_SOURCE.quote, /1:20/);
    assert.match(MGN_RAMP_SLOPE_SOURCE.quote, /1:12/);
  });
});

describe("checklist integration", () => {
  it("topics=['мгн'] selects implemented МГН checkers without МГН skips", () => {
    const checkers = selectPhase1Checkers(["мгн"]);
    assert.deepEqual(
      checkers.map((checker) => checker.checkType).sort(),
      ["mgn_door_width", "mgn_room_geometry"]
    );
    const skipped = selectSkippedRules(["мгн"]);
    const skippedTypes = skipped.map((rule) => rule.checkType);
    assert.equal(skippedTypes.includes("mgn_ramp_slope"), false);
    assert.equal(skippedTypes.includes("mgn_door_maneuvering"), false);
  });

  it("full audit (no topics) includes МГН checkers", () => {
    const checkers = selectPhase1Checkers(undefined);
    const types = checkers.map((checker) => checker.checkType);
    assert.ok(types.includes("mgn_room_geometry"));
    assert.ok(types.includes("mgn_door_width"));
  });
});

describe("measurable ramp and door maneuvering checks", () => {
  it("checks actual ramp slope against 5 percent", () => {
    const result = classifyAccessibilityRamps([
      { id: 1, name: "Пандус 1", slopePercent: 4.8, slopeSource: "geometry_bbox" },
      { id: 2, name: "Пандус 2", slopePercent: 6.0, slopeSource: "parameter:Slope" },
    ]);
    assert.equal(result.findings[0].status, "compliant");
    assert.equal(result.findings[1].status, "violation");
    assert.equal(result.findings[1].requiredMaxPercent, 5);
  });

  it("applies the 8 percent ramp exception only when marked and rise is at most 800 mm", () => {
    const result = classifyAccessibilityRamps([
      { id: 1, slopePercent: 7, riseMm: 700, isExceptionAllowed: false },
      { id: 2, slopePercent: 7, riseMm: 700, isExceptionAllowed: true },
      { id: 3, slopePercent: 7, riseMm: 900, isExceptionAllowed: true },
    ]);
    assert.equal(result.findings[0].status, "violation");
    assert.equal(result.findings[1].status, "compliant");
    assert.equal(result.findings[1].requiredMaxPercent, 8);
    assert.equal(result.findings[1].exceptionApplied, true);
    assert.equal(result.findings[2].status, "violation");
  });

  it("routes MGN rooms, doors, and ramps to the correct highlight mechanism", () => {
    const source = { document: "СП", clause: "1", quote: "норма" };
    const targets = paintTargetsFromFindings([
      {
        checkType: "mgn_turning_circle",
        status: "violation",
        elementId: 10,
        name: "Тамбур",
        level: "1",
        source,
      },
      {
        checkType: "mgn_door_width",
        status: "violation",
        elementId: 20,
        name: "Дверь",
        level: "1",
        source,
      },
      {
        checkType: "mgn_door_maneuvering",
        status: "nearLimit",
        elementId: 20,
        name: "Дверь",
        level: "1",
        source,
      },
      {
        checkType: "mgn_ramp_slope",
        status: "violation",
        elementId: 30,
        name: "Пандус",
        level: "1",
        source,
      },
    ]);
    assert.deepEqual(targets.roomIds, [10]);
    assert.deepEqual(targets.doorIds, [20]);
    assert.deepEqual(targets.otherElementIds, [30]);
  });

  it("checks the limiting maneuvering rectangle and ignores non-egress doors", () => {
    const result = classifyDoorManeuvering([
      {
        id: 1,
        family: "Дверь",
        type: "900",
        isOnEgressPath: true,
        maneuveringDepthMm: 1400,
        maneuveringWidthMm: 1600,
      },
      {
        id: 2,
        isOnEgressPath: false,
        maneuveringDepthMm: 1000,
        maneuveringWidthMm: 1000,
      },
    ]);
    assert.equal(result.findings.length, 1);
    assert.equal(result.findings[0].status, "violation");
    assert.equal(result.findings[0].deviationMm, 100);
    assert.equal(result.nonEgressSkipped, 1);
  });

  it("uses 1200 mm for push-side maneuvering and reports missing geometry", () => {
    const result = classifyDoorManeuvering([
      {
        id: 1,
        isOnEgressPath: true,
        maneuveringDepthMm: 1200,
        maneuveringWidthMm: 1500,
        maneuveringRequiredDepthMm: 1200,
        maneuveringApproach: "push/opposite-facing",
      },
      { id: 2, isOnEgressPath: true },
    ]);
    assert.equal(result.findings[0].status, "compliant");
    assert.equal(result.findings[0].requiredDepthMm, 1200);
    assert.equal(result.findings[0].approach, "push/opposite-facing");
    assert.deepEqual(result.unmeasured.map((door) => door.id), [2]);
  });

  it("treats exact limits with conversion noise as compliant", () => {
    const result = classifyAccessibilityRooms(
      [{ id: 1, name: "Тамбур", widthMm: 1499.999999, depthMm: 2000 }],
      { nearLimitToleranceMm: 50 }
    );
    assert.equal(result.turning[0].status, "compliant");
  });
});
