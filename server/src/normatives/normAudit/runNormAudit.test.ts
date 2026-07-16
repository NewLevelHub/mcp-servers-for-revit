import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { runNormAudit, type NormAuditDeps } from "./runNormAudit.js";
import type { NormAuditSource } from "./types.js";

const source: NormAuditSource = {
  document: "СП РК 3.02-101",
  clause: "п. 6.3.1",
  quote: "Ширина эвакуационного коридора не менее 1200 мм.",
};

function mockDeps(overrides: Partial<NormAuditDeps> = {}): NormAuditDeps {
  return {
    resolveLevelName: async () => "1 этаж",
    runEvacuation: async () => ({
      success: true,
      message: "ok corridors",
      minWidthMm: 1200,
      totalChecked: 2,
      violations: [
        {
          id: 101,
          name: "Тамбур",
          number: "1",
          level: "1 этаж",
          actualWidthMm: 1130,
          requiredWidthMm: 1200,
          deviationMm: 70,
          isCompliant: false,
        },
      ],
      nearLimit: [],
      compliant: [],
      source,
      warnings: [],
    }),
    runRoomDepth: async () => ({
      success: true,
      message: "ok depth",
      totalChecked: 1,
      violations: [],
      compliant: [],
      maxDepthMm: 6000,
      source: {
        document: "СП РК 3.02-101",
        clause: "п. 5.2",
        quote: "Глубина не более 6 м.",
      },
      warnings: [],
    }),
    runMinDimensions: async () => ({
      success: true,
      message: "ok dims",
      totalChecked: 1,
      violations: [
        {
          id: 201,
          name: "Лоджия",
          number: "2",
          level: "1 этаж",
          spaceKind: "loggia",
          metric: "depth",
          actualValueMm: 900,
          requiredValueMm: 1200,
          deviationMm: 300,
          isCompliant: false,
        },
      ],
      compliant: [],
      appliedRules: [
        {
          id: "r1",
          object: "лоджия",
          metric: "depth",
          minValueMm: 1200,
          source: {
            document: "СН РК 3.02-01",
            clause: "п. 4.1",
            quote: "Глубина лоджии не менее 1200 мм.",
          },
          sourcePdf: "test.pdf",
        },
      ],
      fallbackSource: {
        document: "СН РК 3.02-01",
        clause: "п. 4.1",
        quote: "Глубина лоджии не менее 1200 мм.",
      },
      warnings: [],
    }),
    runFireDoors: async () => ({
      success: true,
      message: "ok doors",
      totalChecked: 2,
      doors: [
        {
          id: 301,
          mark: "Д-1",
          type: "ДОмп",
          family: "Дверь",
          level: "1 этаж",
          fromRoom: "Коридор",
          toRoom: "ЛК",
          requiresFireDoor: true,
          compliant: false,
          reason: "выход на лестницу",
          source: {
            document: "СП РК 2.02-101",
            clause: "п. 7.1",
            quote: "Двери на лестницу — противопожарные.",
          },
          markSource: "none",
        },
      ],
      warnings: [],
    }),
    runDoorWidth: async () => ({
      success: true,
      message: "ok doors width",
      minWidthMm: 900,
      totalChecked: 2,
      violations: [
        {
          id: 401,
          uniqueId: "401",
          family: "Дверь",
          type: "700",
          level: "1 этаж",
          status: "violation",
          actualMm: 700,
          requiredMm: 900,
          deviationMm: 200,
          isOnEgressPath: true,
        },
      ],
      nearLimit: [],
      compliant: [],
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.6.11",
        quote: "Ширина дверных проёмов на лестничную клетку не менее 0,9 м.",
      },
      warnings: [],
    }),
    resolveDepthLimit: () => ({
      maxDepthMm: 6000,
      source: {
        document: "СП РК 3.02-101",
        clause: "п. 5.2",
        quote: "Глубина не более 6 м.",
      },
      rule: { id: "depth-1" } as never,
    }),
    resolveDoorWidth: () => ({
      minWidthMm: 900,
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.6.11",
        quote: "Ширина дверных проёмов на лестничную клетку не менее 0,9 м.",
      },
      rule: { id: "door-1" } as never,
    }),
    highlight: async ({ findings }) => ({
      highlightedCount: findings.filter(
        (f) => f.status === "violation" || f.status === "nearLimit"
      ).length,
      filledRegionCount: findings.filter(
        (f) =>
          f.checkType !== "fire_doors" &&
          (f.status === "violation" || f.status === "nearLimit")
      ).length,
      doorCount: findings.filter(
        (f) => f.checkType === "fire_doors" && f.status === "violation"
      ).length,
      message: "mock highlight",
    }),
    db: {} as never,
    ...overrides,
  };
}

describe("runNormAudit orchestrator", () => {
  it("merges all Phase-1 checkers into one findings list with citations", async () => {
    const result = await runNormAudit({ scope: "floor" }, mockDeps());

    assert.equal(result.success, true);
    assert.equal(result.levelName, "1 этаж");
    assert.equal(result.summary.checksRun, 5);
    assert.ok(result.summary.violations >= 4);

    const narrowDoor = result.findings.find(
      (f) => f.checkType === "door_clear_width" && f.elementId === 401
    );
    assert.ok(narrowDoor);
    assert.equal(narrowDoor!.status, "violation");
    assert.equal(narrowDoor!.actualMm, 700);
    assert.equal(narrowDoor!.requiredMm, 900);
    assert.match(narrowDoor!.source.clause, /4\.6\.11/);

    const tambour = result.findings.find((f) => f.name === "Тамбур");
    assert.ok(tambour);
    assert.equal(tambour!.actualMm, 1130);
    assert.equal(tambour!.requiredMm, 1200);
    assert.equal(tambour!.source.document, "СП РК 3.02-101");
    assert.match(tambour!.source.quote, /1200/);

    const loggia = result.findings.find((f) => f.name === "Лоджия");
    assert.ok(loggia);
    assert.equal(loggia!.checkType, "min_dimensions");

    const fire = result.findings.find((f) => f.elementId === 301);
    assert.ok(fire);
    assert.equal(fire!.status, "violation");
    assert.match(fire!.source.quote, /противопожар/);
  });

  it("filters by topics", async () => {
    const result = await runNormAudit(
      { topics: ["эвак. коридор"] },
      mockDeps()
    );
    assert.equal(result.summary.checksRun, 1);
    assert.ok(
      result.findings.every((f) => f.checkType === "evacuation_width")
    );
  });

  it("highlight mode paints unique violation ids once", async () => {
    let paintedFindings = 0;
    const result = await runNormAudit(
      { mode: "highlight", topics: ["коридор", "лоджия", "ПД"] },
      mockDeps({
        highlight: async ({ findings }) => {
          paintedFindings = findings.filter(
            (f) => f.status === "violation" || f.status === "nearLimit"
          ).length;
          return {
            highlightedCount: 2,
            filledRegionCount: 2,
            doorCount: 0,
            message: "ok",
          };
        },
      })
    );
    assert.equal(result.mode, "highlight");
    assert.ok((result.highlightedCount ?? 0) > 0);
    assert.equal(result.filledRegionCount, 2);
    assert.ok(paintedFindings >= 2);
  });

  it("skips room_depth when library has no limit", async () => {
    const result = await runNormAudit(
      { topics: ["глубина"] },
      mockDeps({
        resolveDepthLimit: () => null,
      })
    );
    assert.equal(result.checks[0].status, "skipped");
    assert.equal(result.findings.length, 0);
  });

  it("does not invent success when all checkers error", async () => {
    const result = await runNormAudit(
      {},
      mockDeps({
        runEvacuation: async () => ({
          success: false,
          message: "fail",
          minWidthMm: 0,
          totalChecked: 0,
          violations: [],
          nearLimit: [],
          compliant: [],
          source,
          warnings: [],
        }),
        runRoomDepth: async () => ({
          success: false,
          message: "fail",
          totalChecked: 0,
          violations: [],
          compliant: [],
          source,
          warnings: [],
        }),
        runMinDimensions: async () => ({
          success: false,
          message: "fail",
          totalChecked: 0,
          violations: [],
          compliant: [],
          appliedRules: [],
          fallbackSource: source,
          warnings: [],
        }),
        runFireDoors: async () => ({
          success: false,
          message: "fail",
          totalChecked: 0,
          doors: [],
          warnings: [],
        }),
        runDoorWidth: async () => ({
          success: false,
          message: "fail",
          minWidthMm: 900,
          totalChecked: 0,
          violations: [],
          nearLimit: [],
          compliant: [],
          source,
          warnings: [],
        }),
      })
    );
    assert.equal(result.success, false);
    assert.equal(result.summary.checksFailed, 5);
  });
});
