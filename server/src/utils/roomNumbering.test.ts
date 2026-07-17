import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  buildNumberingPlan,
  orderUnits,
  parseLevelIndex,
  resolveLevelIndexes,
  type NumberingUnit,
} from "./roomNumbering.js";

const unit = (
  id: number,
  x: number,
  y: number,
  level = "1 этаж",
  current = "",
  section?: string
): NumberingUnit => ({
  ids: [id],
  label: `Помещение ${id}`,
  level,
  x,
  y,
  current,
  section,
});

describe("parseLevelIndex / resolveLevelIndexes", () => {
  it("parses digits from level names", () => {
    assert.equal(parseLevelIndex("1 этаж"), 1);
    assert.equal(parseLevelIndex("Этаж 02"), 2);
    assert.equal(parseLevelIndex("L3"), 3);
    assert.equal(parseLevelIndex("Кровля"), null);
  });

  it("assigns stable fallback indexes with a warning", () => {
    const { indexes, warnings } = resolveLevelIndexes(["2 этаж", "Цоколь"]);
    assert.equal(indexes.get("2 этаж"), 2);
    assert.equal(indexes.get("Цоколь"), 1);
    assert.equal(warnings.length, 1);
  });

  it("honors overrides", () => {
    const { indexes, warnings } = resolveLevelIndexes(["Цоколь"], { Цоколь: 0 });
    assert.equal(indexes.get("Цоколь"), 0);
    assert.equal(warnings.length, 0);
  });
});

describe("orderUnits", () => {
  // 2×2 grid: rows y=10000 (top) and y=0.
  const grid = [
    unit(1, 0, 10000),
    unit(2, 5000, 10000),
    unit(3, 0, 0),
    unit(4, 5000, 0),
  ];

  it("snake: top row left→right, next row right→left", () => {
    const ordered = orderUnits(grid, "snake", 3000);
    assert.deepEqual(ordered.map((u) => u.ids[0]), [1, 2, 4, 3]);
  });

  it("clockwise walks around the centroid from 12 o'clock", () => {
    const ordered = orderUnits(grid, "clockwise", 3000);
    assert.deepEqual(ordered.map((u) => u.ids[0]), [2, 4, 3, 1]);
  });

  it("counterclockwise reverses the walk", () => {
    const ordered = orderUnits(grid, "counterclockwise", 3000);
    assert.deepEqual(ordered.map((u) => u.ids[0]), [1, 3, 4, 2]);
  });
});

describe("buildNumberingPlan", () => {
  it("levelPrefix: 101, 102 / 201, 202 by floor", () => {
    const plan = buildNumberingPlan(
      [
        unit(1, 0, 0, "1 этаж"),
        unit(2, 5000, 0, "1 этаж"),
        unit(3, 0, 0, "2 этаж"),
        unit(4, 5000, 0, "2 этаж"),
      ],
      { scheme: "levelPrefix", direction: "snake" }
    );
    assert.deepEqual(
      plan.assignments.map((a) => a.to),
      ["101", "102", "201", "202"]
    );
  });

  it("continuous: сквозная нумерация через этажи в порядке уровней", () => {
    const plan = buildNumberingPlan(
      [
        unit(3, 0, 0, "2 этаж"),
        unit(1, 0, 0, "1 этаж"),
        unit(2, 5000, 0, "1 этаж"),
      ],
      { scheme: "continuous", padWidth: 2 }
    );
    const byId = new Map(plan.assignments.map((a) => [a.ids[0], a.to]));
    assert.equal(byId.get(1), "01");
    assert.equal(byId.get(2), "02");
    assert.equal(byId.get(3), "03");
  });

  it("section prefixes with separator, numbering restarts per section", () => {
    const plan = buildNumberingPlan(
      [
        unit(1, 0, 0, "1 этаж", "", "А"),
        unit(2, 5000, 0, "1 этаж", "", "А"),
        unit(3, 0, 0, "1 этаж", "", "Б"),
      ],
      { scheme: "levelPrefix", useSectionPrefix: true, separator: "-" }
    );
    const byId = new Map(plan.assignments.map((a) => [a.ids[0], a.to]));
    assert.equal(byId.get(1), "А-101");
    assert.equal(byId.get(2), "А-102");
    assert.equal(byId.get(3), "Б-101");
  });

  it("is idempotent: correct numbers are not reassigned", () => {
    const first = buildNumberingPlan(
      [unit(1, 0, 0, "1 этаж", "101"), unit(2, 5000, 0, "1 этаж", "999")],
      {}
    );
    assert.equal(first.unchangedCount, 1, "101 уже верен");
    assert.equal(first.assignments.length, 1);
    assert.equal(first.assignments[0].to, "102");

    // Re-run after applying: everything already correct → no-op.
    const second = buildNumberingPlan(
      [unit(1, 0, 0, "1 этаж", "101"), unit(2, 5000, 0, "1 этаж", "102")],
      {}
    );
    assert.equal(second.assignments.length, 0);
    assert.equal(second.unchangedCount, 2);
  });

  it("re-numbering after inserting a room shifts deterministically", () => {
    const before = buildNumberingPlan(
      [unit(1, 0, 0), unit(3, 10000, 0)],
      {}
    );
    const beforeById = new Map(before.assignments.map((a) => [a.ids[0], a.to]));
    assert.equal(beforeById.get(1), "101");
    assert.equal(beforeById.get(3), "102");

    // A room appears between them → only the shifted tail is rewritten.
    const after = buildNumberingPlan(
      [unit(1, 0, 0, "1 этаж", "101"), unit(2, 5000, 0), unit(3, 10000, 0, "1 этаж", "102")],
      {}
    );
    const afterById = new Map(after.assignments.map((a) => [a.ids[0], a.to]));
    assert.equal(after.unchangedCount, 1, "первое помещение не трогаем");
    assert.equal(afterById.get(2), "102");
    assert.equal(afterById.get(3), "103");
  });

  it("apartment units carry every room id of the group", () => {
    const apartment: NumberingUnit = {
      ids: [11, 12, 13],
      label: "Квартира 7 (3 пом.)",
      level: "1 этаж",
      x: 2000,
      y: 2000,
      current: "7",
      section: undefined,
    };
    const plan = buildNumberingPlan([apartment], { scheme: "continuous" });
    assert.equal(plan.assignments.length, 1);
    assert.deepEqual(plan.assignments[0].ids, [11, 12, 13]);
    assert.equal(plan.assignments[0].to, "1");
  });
});
