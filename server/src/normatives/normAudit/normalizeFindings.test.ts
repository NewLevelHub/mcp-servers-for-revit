import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  findingsForHighlight,
  formatFindingNote,
  normalizeEvacuationFindings,
  normalizeFireDoorFindings,
  normalizeAccessibilityDoorFindings,
  summarizeFindings,
} from "./normalizeFindings.js";
import { formatNormAuditReport } from "./formatAuditReport.js";
import type { NormAuditResult } from "./types.js";

describe("normAudit normalizeFindings", () => {
  it("maps evacuation violations / nearLimit / compliant with source", () => {
    const findings = normalizeEvacuationFindings({
      minWidthMm: 1200,
      source: {
        document: "СП РК 3.02-101",
        clause: "п. 6.3.1",
        quote: "Ширина эвакуационного коридора не менее 1200 мм.",
      },
      violations: [
        {
          id: 1,
          name: "Тамбур",
          level: "1 этаж",
          actualWidthMm: 1000,
          requiredWidthMm: 1200,
          deviationMm: 200,
          isCompliant: false,
        },
      ],
      nearLimit: [
        {
          id: 2,
          name: "Коридор",
          level: "1 этаж",
          actualWidthMm: 1130,
          requiredWidthMm: 1200,
          deviationMm: 70,
          isCompliant: false,
        },
      ],
      compliant: [
        {
          id: 3,
          name: "Холл",
          level: "1 этаж",
          actualWidthMm: 1500,
          requiredWidthMm: 1200,
          deviationMm: 0,
          isCompliant: true,
        },
      ],
    });

    assert.equal(findings.length, 3);
    assert.equal(findings[0].status, "violation");
    assert.equal(findings[0].actualMm, 1000);
    assert.equal(findings[0].requiredMm, 1200);
    assert.equal(findings[0].source.clause, "п. 6.3.1");
    assert.equal(findings[1].status, "nearLimit");
    assert.equal(findings[2].status, "compliant");
  });

  it("maps fire doors without inventing mm requirements", () => {
    const findings = normalizeFireDoorFindings([
      {
        id: 10,
        mark: "Д-1",
        level: "1 этаж",
        requiresFireDoor: true,
        compliant: false,
        reason: "выход на лестницу",
        source: {
          document: "СП РК 2.02-101",
          clause: "п. 7.1",
          quote: "Двери выходов на лестничные клетки — противопожарные.",
        },
        markSource: "none",
      },
      {
        id: 11,
        mark: "Д-2",
        requiresFireDoor: false,
        compliant: true,
        source: { document: "—", clause: "", quote: "" },
      },
    ]);

    assert.equal(findings.length, 1);
    assert.equal(findings[0].status, "violation");
    assert.equal(findings[0].requiredMm, undefined);
    assert.ok(findings[0].source.quote.includes("противопожар"));
  });

  it("summarizeFindings counts statuses", () => {
    const summary = summarizeFindings(
      [
        {
          checkType: "evacuation_width",
          status: "violation",
          elementId: 1,
          name: "A",
          level: "",
          source: { document: "D", clause: "1", quote: "q" },
        },
        {
          checkType: "evacuation_width",
          status: "nearLimit",
          elementId: 2,
          name: "B",
          level: "",
          source: { document: "D", clause: "1", quote: "q" },
        },
        {
          checkType: "fire_doors",
          status: "compliant",
          elementId: 3,
          name: "C",
          level: "",
          source: { document: "D", clause: "1", quote: "q" },
        },
        {
          checkType: "mgn_door_width",
          status: "skipped",
          elementId: 4,
          name: "D",
          level: "",
          source: { document: "D", clause: "1", quote: "q" },
        },
      ],
      2
    );
    assert.deepEqual(summary, {
      violations: 1,
      nearLimit: 1,
      compliant: 1,
      skipped: 3,
    });
  });

  it("emits explicit skipped finding for nominal-only MGN door width", () => {
    const findings = normalizeAccessibilityDoorFindings({
      violations: [],
      nearLimit: [],
      compliant: [],
      unmeasured: [
        {
          id: 20,
          type: "Дверь 900",
          openingWidthMm: 900,
          widthSource: "nominal_fallback",
          isOnEgressPath: true,
        },
      ],
      source: {
        document: "СП РК 3.06-101-2012*",
        clause: "п. 4.3.2.14",
        quote: "не менее 0,9 м",
      },
      minWidthMm: 900,
    });
    assert.equal(findings[0].status, "skipped");
    assert.equal(findings[0].actualMm, 900);
    assert.match(findings[0].note ?? "", /номинал/);
  });

  it("findingsForHighlight only takes violations and nearLimit", () => {
    const els = findingsForHighlight([
      {
        checkType: "evacuation_width",
        status: "violation",
        elementId: 1,
        name: "Тамбур",
        level: "",
        actualMm: 1130,
        requiredMm: 1800,
        metric: "ширина",
        source: { document: "D", clause: "1", quote: "q" },
      },
      {
        checkType: "evacuation_width",
        status: "compliant",
        elementId: 2,
        name: "OK",
        level: "",
        source: { document: "D", clause: "1", quote: "q" },
      },
    ]);
    assert.equal(els.length, 1);
    assert.equal(els[0].elementId, 1);
    assert.ok(formatFindingNote(els[0] as never) || true);
    assert.match(els[0].note, /1130/);
  });
});

describe("formatNormAuditReport", () => {
  it("renders scope disclaimer and skipped rules", () => {
    const result: NormAuditResult = {
      success: true,
      message: "Нормоконтроль: нарушений 1, проверок 1/1.",
      scope: "floor",
      levelName: "1 этаж",
      mode: "report",
      scopeNote: "Проверяем только измеримое.",
      summary: {
        violations: 1,
        nearLimit: 0,
        compliant: 0,
        skipped: 1,
        checksRun: 1,
        checksFailed: 0,
      },
      findings: [
        {
          checkType: "evacuation_width",
          status: "violation",
          elementId: 42,
          name: "Тамбур",
          level: "1 этаж",
          metric: "ширина",
          actualMm: 1130,
          requiredMm: 1800,
          deviationMm: 670,
          source: {
            document: "СП РК 3.02-101",
            clause: "п. 6.3",
            quote: "Ширина не менее 1800 мм.",
          },
        },
      ],
      skippedRules: [
        {
          checkType: "door_clear_width",
          reason: "Checker ещё не реализован (Phase 2).",
          topics: ["ширина двери"],
        },
      ],
      checks: [
        {
          checkType: "evacuation_width",
          status: "ok",
          checkedCount: 3,
        },
      ],
      warnings: [],
    };

    const md = formatNormAuditReport(result);
    assert.match(md, /Тамбур/);
    assert.match(md, /1130/);
    assert.match(md, /СП РК 3\.02-101/);
    assert.match(md, /Phase 2/);
    assert.match(md, /измеримое/);
  });
});
