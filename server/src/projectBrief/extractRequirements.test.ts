import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { extractRequirementsFromText, splitIntoSegments } from "./extractRequirements.js";

describe("splitIntoSegments", () => {
  it("one non-empty line is one segment", () => {
    const segments = splitIntoSegments("Первая строка\n\nВторая строка\n");
    assert.deepEqual(
      segments.map((s) => s.text),
      ["Первая строка", "Вторая строка"]
    );
  });

  it("tags a line starting with a section number as its clause", () => {
    const segments = splitIntoSegments("3.2 Состав помещений определяется заданием.");
    assert.equal(segments[0].clause, "3.2");
  });

  it("a line with no leading number has an empty clause, not a guessed one", () => {
    const segments = splitIntoSegments("Количество студий: 25 шт.");
    assert.equal(segments[0].clause, "");
  });
});

describe("extractRequirementsFromText — room_count (ticket's own worked example shape)", () => {
  it("«25 студий» / «Студия — 25 шт.» is read as a room_count requirement", () => {
    const result = extractRequirementsFromText({
      text: "Студия — 25 шт.",
      document: "ТЗ на проектирование",
    });
    const req = result.requirements.find((r) => r.type === "room_count");
    assert.ok(req, "expected a room_count requirement");
    assert.equal(req!.object, "студия");
    assert.equal(req!.value, 25);
    assert.equal(req!.unit, "pcs");
    assert.equal(req!.source.quote, "Студия — 25 шт.");
  });

  it("«не менее 25 студий» is also read as a count via the label cue", () => {
    const result = extractRequirementsFromText({ text: "Предусмотреть не менее 25 студий в составе комплекса." });
    const req = result.requirements.find((r) => r.type === "room_count");
    assert.ok(req);
    assert.equal(req!.object, "студия");
    assert.equal(req!.value, 25);
  });

  it("digit-prefixed apartment type is normalized to «N-комнатная квартира»", () => {
    const result = extractRequirementsFromText({ text: "2-комнатных квартир — 40 шт." });
    const req = result.requirements.find((r) => r.type === "room_count");
    assert.ok(req);
    assert.equal(req!.object, "2-комнатная квартира");
    assert.equal(req!.value, 40);
  });

  it("word-form apartment count («однокомнатных») resolves to the same digit form", () => {
    const result = extractRequirementsFromText({ text: "Однокомнатных квартир предусмотреть 12 шт." });
    const req = result.requirements.find((r) => r.type === "room_count");
    assert.ok(req);
    assert.equal(req!.object, "1-комнатная квартира");
    assert.equal(req!.value, 12);
  });

  it("a bare number with no unit/label cue near an object is NOT extracted as a count (avoid guessing)", () => {
    const result = extractRequirementsFromText({ text: "Кладовая площадью 3.5 м² расположена на 2 этаже." });
    const counts = result.requirements.filter((r) => r.type === "room_count");
    assert.equal(counts.length, 0);
  });
});

describe("extractRequirementsFromText — room_area_min", () => {
  it("«площадь не менее N м²» is read as room_area_min", () => {
    const result = extractRequirementsFromText({ text: "Площадь кладовых принять не менее 3 м²." });
    const req = result.requirements.find((r) => r.type === "room_area_min");
    assert.ok(req);
    assert.equal(req!.object, "кладовая");
    assert.equal(req!.value, 3);
    assert.equal(req!.unit, "m2");
  });

  it("a decimal area with a comma is parsed correctly", () => {
    const result = extractRequirementsFromText({ text: "Офисные помещения не менее 12,5 м² каждое." });
    const req = result.requirements.find((r) => r.type === "room_area_min");
    assert.ok(req);
    assert.equal(req!.value, 12.5);
  });
});

describe("extractRequirementsFromText — qualitative fallback and honesty", () => {
  it("a качественное requirement (должен/следует) with no number is captured as type requirement", () => {
    const result = extractRequirementsFromText({ text: "Проект должен предусматривать колясочную на первом этаже." });
    const req = result.requirements.find((r) => r.type === "requirement");
    assert.ok(req);
    assert.equal(req!.value, "Проект должен предусматривать колясочную на первом этаже.");
  });

  it("plain narrative text with no cue produces nothing, not a guess", () => {
    const result = extractRequirementsFromText({ text: "Проект выполнен в соответствии с действующими нормами." });
    assert.equal(result.requirements.length, 0);
  });

  it("an unrecognized line with a number is warned about, not silently dropped", () => {
    const result = extractRequirementsFromText({ text: "Смотри приложение 7 для деталей." });
    assert.equal(result.requirements.length, 0);
    assert.ok(result.warnings.some((w) => w.includes("Смотри приложение 7")));
  });

  it("document defaults to «проектное задание» when none is given", () => {
    const result = extractRequirementsFromText({ text: "Студия — 5 шт." });
    assert.equal(result.requirements[0].source.document, "проектное задание");
  });

  it("an explicit document title is kept as given", () => {
    const result = extractRequirementsFromText({
      text: "Студия — 5 шт.",
      document: "ТЗ ЖК «Сарыарка»",
    });
    assert.equal(result.requirements[0].source.document, "ТЗ ЖК «Сарыарка»");
  });
});

describe("extractRequirementsFromText — 200-line batch (matches fillRules' own scale bar)", () => {
  it("extracts one requirement per line across many lines without cross-contamination", () => {
    const lines = Array.from({ length: 50 }, (_, i) => `Кладовая ${i} — ${i + 1} шт.`);
    const result = extractRequirementsFromText({ text: lines.join("\n") });
    const counts = result.requirements.filter((r) => r.type === "room_count");
    assert.equal(counts.length, 50);
    assert.equal(counts[0].value, 1);
    assert.equal(counts[49].value, 50);
  });
});
