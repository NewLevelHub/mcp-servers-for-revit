import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { buildEdits } from "./set_elements_parameters.js";

/**
 * Exercises the real exported helper rather than a copy of the schema, so the
 * test cannot quietly drift away from what the tool does.
 */
describe("set_elements_parameters — folding the two call shapes", () => {
  it("passes per-element edits through unchanged", () => {
    const built = buildEdits({
      edits: [
        { elementId: 101, parameters: { Марка: "Д-1" } },
        { elementId: 102, parameters: { Марка: "Д-2" } },
      ],
    });

    assert.ok(!("error" in built));
    assert.deepEqual(built.edits, [
      { elementId: 101, parameters: { Марка: "Д-1" } },
      { elementId: 102, parameters: { Марка: "Д-2" } },
    ]);
  });

  it("expands the elementIds shorthand to one edit per element", () => {
    const built = buildEdits({
      elementIds: [7, 8, 9],
      parameters: { "Предел огнестойкости": "EI 30" },
    });

    assert.ok(!("error" in built));
    assert.equal(built.edits.length, 3);
    assert.deepEqual(
      built.edits.map((e) => e.elementId),
      [7, 8, 9]
    );
    for (const edit of built.edits) {
      assert.deepEqual(edit.parameters, { "Предел огнестойкости": "EI 30" });
    }
  });

  it("prefers edits over the shorthand when both are given", () => {
    const built = buildEdits({
      edits: [{ elementId: 1, parameters: { Mark: "A" } }],
      elementIds: [2, 3],
      parameters: { Mark: "B" },
    });

    assert.ok(!("error" in built));
    assert.deepEqual(built.edits, [{ elementId: 1, parameters: { Mark: "A" } }]);
  });

  it("keeps mixed value types intact for the Revit side to convert", () => {
    const built = buildEdits({
      edits: [
        {
          elementId: 5,
          parameters: { Mark: "Д-1", Height: 2100, "Room Bounding": true },
        },
      ],
    });

    assert.ok(!("error" in built));
    assert.deepEqual(built.edits[0].parameters, {
      Mark: "Д-1",
      Height: 2100,
      "Room Bounding": true,
    });
  });

  it("refuses an empty call instead of spending a Revit round trip", () => {
    const built = buildEdits({});
    assert.ok("error" in built);
    assert.match(built.error, /Нечего записывать/);
  });

  it("refuses the shorthand without parameters, naming the element", () => {
    const built = buildEdits({ elementIds: [42] });
    assert.ok("error" in built);
    assert.match(built.error, /42/);
  });

  it("refuses an edit whose parameters object is empty", () => {
    const built = buildEdits({
      edits: [
        { elementId: 11, parameters: { Mark: "ok" } },
        { elementId: 12, parameters: {} },
      ],
    });

    assert.ok("error" in built);
    assert.match(built.error, /12/);
  });
});
