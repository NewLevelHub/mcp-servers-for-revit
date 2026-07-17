import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  buildAutoNumberPlan,
  chunk,
  naturalCompareSheetNumbers,
  PROJECT_FIELD_ALIASES,
  resolveParameterName,
  SHEET_FIELD_ALIASES,
} from "./titleBlock.js";

describe("resolveParameterName", () => {
  const available = [
    { name: "Разработал", isReadOnly: false },
    { name: "Sheet Number", isReadOnly: false },
    { name: "Масштаб", isReadOnly: true },
  ];

  it("matches aliases case-insensitively in priority order", () => {
    assert.equal(
      resolveParameterName(available, SHEET_FIELD_ALIASES.drawnBy).name,
      "Разработал"
    );
    assert.equal(
      resolveParameterName(available, ["РАЗРАБОТАЛ"]).name,
      "Разработал"
    );
  });

  it("reports read-only matches instead of writing them", () => {
    const resolved = resolveParameterName(available, ["Масштаб"]);
    assert.equal(resolved.name, undefined);
    assert.equal(resolved.readOnlyMatch, "Масштаб");
  });

  it("returns nothing when the template lacks the field", () => {
    const resolved = resolveParameterName(available, PROJECT_FIELD_ALIASES.stage);
    assert.equal(resolved.name, undefined);
    assert.equal(resolved.readOnlyMatch, undefined);
  });
});

describe("naturalCompareSheetNumbers", () => {
  it("orders numeric parts numerically", () => {
    const numbers = ["АР-10", "АР-2", "АР-1", "10", "9"];
    numbers.sort(naturalCompareSheetNumbers);
    assert.deepEqual(numbers, ["9", "10", "АР-1", "АР-2", "АР-10"]);
  });
});

describe("buildAutoNumberPlan", () => {
  it("renumbers in natural order with prefix and padding", () => {
    const plan = buildAutoNumberPlan(
      [
        { id: 1, number: "АР-3" },
        { id: 2, number: "АР-1" },
        { id: 3, number: "АР-10" },
      ],
      { startNumber: 1, prefix: "АР-", padWidth: 2 }
    );
    assert.deepEqual(
      plan.finalNumbers,
      [
        { id: 2, number: "АР-01" },
        { id: 1, number: "АР-02" },
        { id: 3, number: "АР-03" },
      ]
    );
  });

  it("skips sheets whose number is already correct", () => {
    const plan = buildAutoNumberPlan(
      [
        { id: 1, number: "1" },
        { id: 2, number: "5" },
      ],
      { startNumber: 1 }
    );
    assert.equal(plan.assignments.length, 1);
    assert.deepEqual(plan.assignments[0], { id: 2, from: "5", to: "2" });
    assert.equal(plan.tempAssignments.length, 0);
  });

  it("routes colliding renumbers through temp numbers (unique constraint)", () => {
    // Shift 2→1, 3→2: writing «2» while another sheet still holds «2» must not happen.
    const plan = buildAutoNumberPlan(
      [
        { id: 1, number: "2" },
        { id: 2, number: "3" },
      ],
      { startNumber: 1 }
    );
    assert.equal(plan.assignments.length, 2);
    assert.equal(plan.tempAssignments.length, 2);
    assert.ok(plan.tempAssignments.every((t) => t.to.startsWith("MCPTMP-")));
  });

  it("no temp pass when targets are all free", () => {
    const plan = buildAutoNumberPlan(
      [
        { id: 1, number: "А" },
        { id: 2, number: "Б" },
      ],
      { startNumber: 1, prefix: "АР-" }
    );
    assert.equal(plan.tempAssignments.length, 0);
    assert.equal(plan.assignments.length, 2);
  });
});

describe("chunk", () => {
  it("splits into batch-sized groups", () => {
    assert.deepEqual(chunk([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
    assert.deepEqual(chunk([], 20), []);
  });
});
