import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  classifyTambourSizes,
  isTambourRoom,
  type TambourRoomInput,
} from "./tambourSize.js";

describe("isTambourRoom", () => {
  it("matches тамбур / vestibule room names", () => {
    assert.equal(isTambourRoom("Тамбур входной", "12"), true);
    assert.equal(isTambourRoom("Vestibule", ""), true);
    assert.equal(isTambourRoom("", "Тамбұр 1"), true);
  });

  it("does not match unrelated rooms", () => {
    assert.equal(isTambourRoom("Кухня", "1"), false);
    assert.equal(isTambourRoom("Коридор", "2"), false);
  });
});

describe("classifyTambourSizes (golden fixtures)", () => {
  const rooms: TambourRoomInput[] = [
    {
      id: 1,
      name: "Тамбур входной",
      widthMm: 1700,
      depthMm: 1800,
    },
    {
      id: 2,
      name: "Тамбур",
      widthMm: 1400,
      depthMm: 1900,
    },
    {
      id: 3,
      name: "Тамбур",
      widthMm: 1620,
      depthMm: 1700,
    },
    {
      id: 4,
      name: "Кухня",
      widthMm: 1200,
      depthMm: 1200,
    },
    {
      id: 5,
      name: "Vestibule",
      widthMm: 0,
      depthMm: 1700,
    },
  ];

  it("flags narrow tambour by min side, keeps ok as compliant", () => {
    const result = classifyTambourSizes(rooms, {
      minSideMm: 1650,
      nearLimitToleranceMm: 50,
    });

    assert.equal(result.tamboursFound, 4);
    assert.equal(result.violations.length, 1);
    assert.equal(result.violations[0].id, 2);
    assert.equal(result.violations[0].minSideMm, 1400);
    assert.equal(result.violations[0].deviationMm, 250);

    assert.equal(result.nearLimit.length, 1);
    assert.equal(result.nearLimit[0].id, 3);

    assert.equal(result.compliant.length, 1);
    assert.equal(result.compliant[0].id, 1);
  });

  it("skips non-tambour rooms and missing geometry", () => {
    const result = classifyTambourSizes(rooms, { minSideMm: 1650 });
    assert.ok(!result.violations.some((r) => r.id === 4));
    assert.equal(result.missingGeometry, 1);
  });
});
