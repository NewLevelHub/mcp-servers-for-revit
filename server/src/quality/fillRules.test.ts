import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { extractTokens, resolveTemplate, hasValue, planFill } from "./fillRules.js";

describe("extractTokens", () => {
  it("finds a single token", () => {
    assert.deepEqual(extractTokens("{Тип}"), ["Тип"]);
  });

  it("finds multiple distinct tokens in order", () => {
    assert.deepEqual(extractTokens("{Тип} {Толщина}мм"), ["Тип", "Толщина"]);
  });

  it("de-duplicates a repeated token", () => {
    assert.deepEqual(extractTokens("{Марка}-{Марка}"), ["Марка"]);
  });

  it("trims whitespace inside braces", () => {
    assert.deepEqual(extractTokens("{ Тип }"), ["Тип"]);
  });

  it("returns empty for a literal-only template", () => {
    assert.deepEqual(extractTokens("Стена типовая"), []);
  });
});

describe("resolveTemplate", () => {
  it("substitutes the ticket's own example: «Наименование = Тип + толщина»", () => {
    const result = resolveTemplate("{Тип} {Толщина}мм", { Тип: "Кирпич", Толщина: 200 });
    assert.equal(result.value, "Кирпич 200мм");
    assert.deepEqual(result.missingFields, []);
  });

  it("reports a missing field instead of silently leaving a gap", () => {
    const result = resolveTemplate("{Тип} {Толщина}мм", { Тип: "Кирпич" });
    assert.deepEqual(result.missingFields, ["Толщина"]);
  });

  it("treats a whitespace-only field as missing, not as an empty-but-present value", () => {
    const result = resolveTemplate("{Тип}", { Тип: "   " });
    assert.deepEqual(result.missingFields, ["Тип"]);
  });

  it("a template with no tokens resolves to itself with nothing missing", () => {
    const result = resolveTemplate("Фиксированное имя", {});
    assert.equal(result.value, "Фиксированное имя");
    assert.deepEqual(result.missingFields, []);
  });

  it("reports each distinct missing field once even if referenced twice", () => {
    const result = resolveTemplate("{X}-{X}", {});
    assert.deepEqual(result.missingFields, ["X"]);
  });
});

describe("hasValue", () => {
  it("undefined/null/empty/whitespace are all 'no value'", () => {
    assert.equal(hasValue(undefined), false);
    assert.equal(hasValue(null), false);
    assert.equal(hasValue(""), false);
    assert.equal(hasValue("   "), false);
  });

  it("a real string or number is a value, including 0", () => {
    assert.equal(hasValue("x"), true);
    assert.equal(hasValue(0), true);
    assert.equal(hasValue(200), true);
  });
});

describe("planFill", () => {
  const elements = [
    { elementId: 1, fields: { Тип: "Кирпич", Толщина: 200, Наименование: "" } },
    { elementId: 2, fields: { Тип: "Бетон", Толщина: 300, Наименование: "Уже заполнено" } },
    { elementId: 3, fields: { Тип: "Газоблок", Наименование: "" } }, // Толщина missing
  ];

  it("fills an empty target and reports the old/new value", () => {
    const plan = planFill("{Тип} {Толщина}мм", "Наименование", elements, false);
    const row1 = plan.find((r) => r.elementId === 1)!;
    assert.equal(row1.newValue, "Кирпич 200мм");
    assert.equal(row1.skip, undefined);
  });

  it("skips an element that already has a value, without overwrite", () => {
    const plan = planFill("{Тип} {Толщина}мм", "Наименование", elements, false);
    const row2 = plan.find((r) => r.elementId === 2)!;
    assert.equal(row2.skip, "already-has-value");
    assert.equal(row2.newValue, undefined);
    assert.equal(row2.currentValue, "Уже заполнено");
  });

  it("overwrite:true fills even an element with an existing value", () => {
    const plan = planFill("{Тип} {Толщина}мм", "Наименование", elements, true);
    const row2 = plan.find((r) => r.elementId === 2)!;
    assert.equal(row2.skip, undefined);
    assert.equal(row2.newValue, "Бетон 300мм");
  });

  it("skips an element missing a source field the template needs", () => {
    const plan = planFill("{Тип} {Толщина}мм", "Наименование", elements, false);
    const row3 = plan.find((r) => r.elementId === 3)!;
    assert.equal(row3.skip, "missing-source-field");
    assert.deepEqual(row3.missingFields, ["Толщина"]);
  });

  it("200 elements resolve in one call — no batching decision made at this layer", () => {
    const many = Array.from({ length: 200 }, (_, i) => ({
      elementId: i + 1,
      fields: { Тип: "Кирпич", Толщина: 200 },
    }));
    const plan = planFill("{Тип} {Толщина}мм", "Наименование", many, false);
    assert.equal(plan.length, 200);
    assert.equal(plan.every((r) => r.newValue === "Кирпич 200мм"), true);
  });
});
