import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  formatFindingAnnotation,
  findingsToAnnotationNotes,
} from "./formatFindingAnnotation.js";
import type { NormAuditFinding } from "./types.js";

function finding(
  partial: Partial<NormAuditFinding> &
    Pick<NormAuditFinding, "elementId" | "name">
): NormAuditFinding {
  return {
    checkType: "min_dimensions",
    status: "violation",
    level: "2 этаж",
    source: {
      document: "СП РК 3.02-101",
      clause: "п.4.6.5",
      quote: "Очень длинная цитата которую нельзя копировать на план целиком.",
    },
    ...partial,
  };
}

describe("formatFindingAnnotation", () => {
  it("formats linear comparison with document/clause", () => {
    const text = formatFindingAnnotation(
      finding({
        elementId: 10,
        name: "Лоджия",
        metric: "ширина",
        actualMm: 1130,
        requiredMm: 1400,
      })
    );
    assert.equal(
      text,
      "Лоджия: ширина 1130 < 1400 мм · СП РК 3.02-101 п.4.6.5"
    );
    assert.ok(!text.includes("длинная цитата"));
  });

  it("formats room area in m²", () => {
    const text = formatFindingAnnotation(
      finding({
        elementId: 11,
        checkType: "room_area_min",
        name: "Кухня",
        metric: "площадь",
        actualMm: 4.2,
        requiredMm: 8,
        source: { document: "СП РК 3.02-101", clause: "п.4.3", quote: "…" },
      })
    );
    assert.equal(text, "Кухня: 4.2 < 8 м² · СП РК 3.02-101 п.4.3");
  });

  it("uses note when no actual/required (fire door)", () => {
    const text = formatFindingAnnotation(
      finding({
        elementId: 12,
        checkType: "fire_doors",
        name: "Дверь 12",
        note: "Нет признака ПД",
        source: { document: "СП РК 2.02-101", clause: "п.5.1", quote: "…" },
      })
    );
    assert.equal(
      text,
      "Дверь 12: Нет признака ПД · СП РК 2.02-101 п.5.1"
    );
  });

  it("uses > when actual exceeds max required", () => {
    const text = formatFindingAnnotation(
      finding({
        elementId: 13,
        checkType: "room_depth",
        name: "Гостиная",
        metric: "глубина",
        actualMm: 7500,
        requiredMm: 6000,
        source: { document: "СП РК 3.02-101", clause: "п.4.4", quote: "…" },
      })
    );
    assert.equal(
      text,
      "Гостиная: глубина 7500 > 6000 мм · СП РК 3.02-101 п.4.4"
    );
  });
});

describe("findingsToAnnotationNotes", () => {
  it("keeps violation and nearLimit by default", () => {
    const notes = findingsToAnnotationNotes([
      finding({
        elementId: 1,
        name: "A",
        status: "violation",
        actualMm: 1,
        requiredMm: 2,
      }),
      finding({
        elementId: 2,
        name: "B",
        status: "nearLimit",
        actualMm: 1,
        requiredMm: 2,
      }),
      finding({
        elementId: 3,
        name: "C",
        status: "compliant",
        actualMm: 3,
        requiredMm: 2,
      }),
    ]);
    assert.deepEqual(
      notes.map((n) => n.elementId),
      [1, 2]
    );
    assert.ok(notes[0].text.includes("A:"));
  });
});
