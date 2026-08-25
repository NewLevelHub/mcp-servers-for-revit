import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { checkCompleteness } from "./dataCompleteness.js";

describe("checkCompleteness", () => {
  const elements = [
    { elementId: 1, fields: { Марка: "Д-1", Изготовитель: "ООО Ромашка" } },
    { elementId: 2, fields: { Марка: "Д-2", Изготовитель: "" } },
    { elementId: 3, fields: { Марка: "", Изготовитель: "" } },
  ];

  it("names the specific elements and fields, not just a count", () => {
    const report = checkCompleteness(["Марка", "Изготовитель"], elements);
    assert.equal(report.incompleteCount, 2);
    assert.deepEqual(
      report.elements.map((e) => e.elementId),
      [2, 3]
    );
    assert.deepEqual(report.elements.find((e) => e.elementId === 2)!.missingFields, ["Изготовитель"]);
    assert.deepEqual(report.elements.find((e) => e.elementId === 3)!.missingFields, ["Марка", "Изготовитель"]);
  });

  it("byField counts how many elements are missing each field", () => {
    const report = checkCompleteness(["Марка", "Изготовитель"], elements);
    assert.deepEqual(report.byField, { Изготовитель: 2, Марка: 1 });
  });

  it("a field nobody is missing does not appear in byField at all", () => {
    const report = checkCompleteness(["Марка"], [{ elementId: 1, fields: { Марка: "Д-1" } }]);
    assert.deepEqual(report.byField, {});
  });

  it("totalChecked/completeCount/incompleteCount add up", () => {
    const report = checkCompleteness(["Марка", "Изготовитель"], elements);
    assert.equal(report.totalChecked, 3);
    assert.equal(report.completeCount + report.incompleteCount, report.totalChecked);
  });

  it("an element with no required fields missing is not in the report", () => {
    const report = checkCompleteness(["Марка"], elements);
    assert.equal(
      report.elements.some((e) => e.elementId === 1),
      false
    );
  });

  it("whitespace-only counts as missing, same as fillRules' hasValue", () => {
    const report = checkCompleteness(["Марка"], [{ elementId: 9, fields: { Марка: "   " } }]);
    assert.equal(report.incompleteCount, 1);
  });

  it("no required parameters means nothing can be incomplete", () => {
    const report = checkCompleteness([], elements);
    assert.equal(report.incompleteCount, 0);
  });
});
