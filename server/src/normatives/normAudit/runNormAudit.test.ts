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
      totalChecked: 3,
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
    runTambourSize: async () => ({
      success: true,
      message: "ok tambour",
      minSideMm: 1650,
      totalChecked: 1,
      violations: [
        {
          id: 501,
          uniqueId: "501",
          name: "Тамбур входной",
          level: "1 этаж",
          status: "violation",
          widthMm: 1400,
          depthMm: 1900,
          minSideMm: 1400,
          requiredSideMm: 1650,
          deviationMm: 250,
        },
      ],
      nearLimit: [],
      compliant: [],
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4.10.6",
        quote: "Тамбур не менее 1,65 м × 1,65 м.",
      },
      warnings: [],
    }),
    resolveTambourSize: () => ({
      minSideMm: 1650,
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4.10.6",
        quote: "Тамбур не менее 1,65 м × 1,65 м.",
      },
      rule: { id: "tambour-1" } as never,
    }),
    runAccessibilityRooms: async () => ({
      success: true,
      message: "ok mgn rooms",
      totalChecked: 1,
      turning: [
        {
          id: 801,
          uniqueId: "801",
          name: "Тамбур МГН",
          level: "1 этаж",
          status: "violation" as const,
          widthMm: 1300,
          depthMm: 2000,
          actualMm: 1300,
          requiredMm: 1500,
          deviationMm: 200,
          note: "min сторона 1300 мм (зона разворота ⌀ 1500 мм)",
        },
      ],
      corridors: [],
      wc: [],
      warnings: [],
    }),
    runAccessibilityDoors: async () => ({
      success: true,
      message: "ok mgn doors",
      minWidthMm: 900,
      totalChecked: 3,
      violations: [
        {
          id: 901,
          uniqueId: "901",
          family: "Дверь",
          type: "800",
          level: "1 этаж",
          status: "violation" as const,
          actualMm: 800,
          requiredMm: 900,
          deviationMm: 100,
          isOnEgressPath: true,
        },
      ],
      nearLimit: [],
      compliant: [],
      ramps: [
        {
          id: 902,
          uniqueId: "902",
          name: "Пандус",
          level: "1 этаж",
          status: "violation" as const,
          slopePercent: 6,
          requiredMaxPercent: 5,
          deviationPercent: 1,
          slopeSource: "geometry_bbox",
          exceptionApplied: false,
        },
      ],
      maneuvering: [
        {
          id: 901,
          uniqueId: "901",
          name: "Дверь 800",
          level: "1 этаж",
          status: "violation" as const,
          actualDepthMm: 1300,
          actualWidthMm: 1500,
          requiredDepthMm: 1500,
          requiredWidthMm: 1500,
          deviationMm: 200,
          roomName: "Коридор",
          approach: "pull/family-facing",
        },
      ],
      source: {
        document: "СП РК 3.06-101-2012*",
        clause: "п. 4.3.2.14",
        quote: "Ширина двери в помещении должна быть не менее 0,9 м.",
      },
      warnings: [],
    }),
    runRoomArea: async () => ({
      success: true,
      message: "ok area",
      limits: [
        {
          category: "living_room" as const,
          minAreaM2: 9,
          source: {
            document: "СП РК 3.02-101-2012",
            clause: "п. 5.1.2",
            quote: "9 м²",
          },
          rule: { id: "area-1" } as never,
        },
      ],
      totalChecked: 1,
      violations: [
        {
          id: 601,
          name: "Жилая",
          level: "1 этаж",
          status: "violation" as const,
          areaM2: 8,
          requiredAreaM2: 9,
          deviationM2: 1,
          category: "living_room" as const,
          limitSource: {
            category: "living_room" as const,
            minAreaM2: 9,
            source: {
              document: "СП РК 3.02-101-2012",
              clause: "п. 5.1.2",
              quote: "9 м²",
            },
            rule: { id: "area-1" } as never,
          },
        },
      ],
      nearLimit: [],
      compliant: [],
      warnings: [],
    }),
    runRoomHeight: async () => ({
      success: true,
      message: "ok height",
      minHeightMm: 2500,
      totalChecked: 1,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4.10",
        quote: "2500 мм",
      },
      warnings: [],
    }),
    runStoreyHeight: async () => ({
      success: true,
      message: "ok storey",
      minStoreyHeightMm: 2800,
      totalChecked: 1,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4",
        quote: "2800 мм",
      },
      warnings: [],
    }),
    resolveRoomAreaLimits: () => [
      {
        category: "living_room" as const,
        minAreaM2: 9,
        source: {
          document: "СП РК 3.02-101-2012",
          clause: "п. 5.1.2",
          quote: "9 м²",
        },
        rule: { id: "area-1" } as never,
      },
    ],
    resolveRoomHeight: () => ({
      minHeightMm: 2500,
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4.10",
        quote: "2500 мм",
      },
      rule: { id: "height-1" } as never,
    }),
    resolveStoreyHeight: () => ({
      minStoreyHeightMm: 2800,
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4",
        quote: "2800 мм",
      },
      rule: { id: "storey-1" } as never,
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
    assert.equal(result.summary.checksRun, 11);
    assert.ok(result.summary.violations >= 5);

    const mgnTambour = result.findings.find(
      (f) => f.checkType === "mgn_turning_circle" && f.elementId === 801
    );
    assert.ok(mgnTambour);
    assert.equal(mgnTambour!.requiredMm, 1500);
    assert.match(mgnTambour!.source.document, /3\.06-101-2012/);

    const mgnDoor = result.findings.find(
      (f) => f.checkType === "mgn_door_width" && f.elementId === 901
    );
    assert.ok(mgnDoor);
    assert.match(mgnDoor!.source.quote, /0,9 м/);

    const ramp = result.findings.find(
      (f) => f.checkType === "mgn_ramp_slope" && f.elementId === 902
    );
    assert.ok(ramp);
    assert.equal(ramp!.actualMm, 6);
    assert.equal(ramp!.requiredMm, 5);
    assert.match(ramp!.source.clause, /4\.3\.2\.30/);

    const maneuvering = result.findings.find(
      (f) => f.checkType === "mgn_door_maneuvering" && f.elementId === 901
    );
    assert.ok(maneuvering);
    assert.match(maneuvering!.source.clause, /4\.3\.2\.13/);

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
    assert.equal(tambour!.checkType, "evacuation_width");
    assert.equal(tambour!.actualMm, 1130);
    assert.equal(tambour!.requiredMm, 1200);
    assert.equal(tambour!.source.document, "СП РК 3.02-101");
    assert.match(tambour!.source.quote, /1200/);

    const tambourSize = result.findings.find(
      (f) => f.checkType === "tambour_size_min" && f.elementId === 501
    );
    assert.ok(tambourSize);
    assert.equal(tambourSize!.actualMm, 1400);
    assert.equal(tambourSize!.requiredMm, 1650);
    assert.match(tambourSize!.source.clause, /4\.4\.10/);

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
        runTambourSize: async () => ({
          success: false,
          message: "fail",
          minSideMm: 1650,
          totalChecked: 0,
          violations: [],
          nearLimit: [],
          compliant: [],
          source,
          warnings: [],
        }),
        runAccessibilityRooms: async () => ({
          success: false,
          message: "fail",
          totalChecked: 0,
          turning: [],
          corridors: [],
          wc: [],
          warnings: [],
        }),
        runAccessibilityDoors: async () => ({
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
        runRoomArea: async () => ({
          success: false,
          message: "fail",
          limits: [],
          totalChecked: 0,
          violations: [],
          nearLimit: [],
          compliant: [],
          warnings: [],
        }),
        runRoomHeight: async () => ({
          success: false,
          message: "fail",
          minHeightMm: 2500,
          totalChecked: 0,
          violations: [],
          nearLimit: [],
          compliant: [],
          source,
          warnings: [],
        }),
        runStoreyHeight: async () => ({
          success: false,
          message: "fail",
          minStoreyHeightMm: 2800,
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
    assert.equal(result.summary.checksFailed, 11);
  });
});
