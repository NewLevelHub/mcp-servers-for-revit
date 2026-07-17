import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  classifyAccessibilityRooms,
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
  it("topics=['мгн'] selects only МГН checkers and skips", () => {
    const checkers = selectPhase1Checkers(["мгн"]);
    assert.deepEqual(
      checkers.map((checker) => checker.checkType).sort(),
      ["mgn_door_width", "mgn_room_geometry"]
    );
    const skipped = selectSkippedRules(["мгн"]);
    const skippedTypes = skipped.map((rule) => rule.checkType);
    assert.ok(skippedTypes.includes("mgn_ramp_slope"));
    assert.ok(skippedTypes.includes("mgn_door_maneuvering"));
  });

  it("full audit (no topics) includes МГН checkers", () => {
    const checkers = selectPhase1Checkers(undefined);
    const types = checkers.map((checker) => checker.checkType);
    assert.ok(types.includes("mgn_room_geometry"));
    assert.ok(types.includes("mgn_door_width"));
  });
});
