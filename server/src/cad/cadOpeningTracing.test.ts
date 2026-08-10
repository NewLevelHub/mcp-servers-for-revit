import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { CadSegment } from "./cadWallTracing.js";
import {
  filterSegmentsForOpenings,
  clusterOpeningSegments,
  openingsFromClusters,
  dedupeOpenings,
  traceOpeningsFromCad,
  matchOpeningToHost,
  matchOpeningsToHosts,
  projectPointOntoWall,
  parseOpeningTypeWidthMm,
  matchOpeningTypeByWidth,
  verifyOpeningsAgainstHosts,
  estimateOpeningWidthMm,
  type HostWall,
} from "./cadOpeningTracing.js";

function seg(
  x0: number,
  y0: number,
  x1: number,
  y1: number,
  layer: string,
  cadId?: string
): CadSegment {
  return {
    startMm: { x: x0, y: y0, z: 0 },
    endMm: { x: x1, y: y1, z: 0 },
    lengthMm: Math.hypot(x1 - x0, y1 - y0),
    layer,
    cadId: cadId ?? `${layer}-${x0}-${y0}`,
  };
}

describe("cadOpeningTracing (REV-147)", () => {
  it("filters door layers and excludes curtain glazing", () => {
    const segments = [
      seg(0, 0, 900, 0, "A-DOOR", "d1"),
      seg(0, 0, 0, 900, "A-DOOR", "d2"),
      seg(0, 0, 3000, 0, "A-WALL", "w1"),
      seg(0, 0, 2000, 0, "A-GLAZ-CURT", "c1"),
    ];
    const filtered = filterSegmentsForOpenings(segments, {
      layerPatterns: ["a-door", "door"],
    });
    assert.equal(filtered.segments.length, 2);
    assert.ok(filtered.segments.every((s) => s.layer === "A-DOOR"));
  });

  it("clusters door swing symbol into one opening with host", () => {
    // Door leaf on wall (horizontal) + longer vertical swing — must prefer leaf
    const segments = [
      seg(2000, 0, 2900, 0, "A-DOOR", "leaf"),
      seg(2000, 50, 2900, 50, "A-DOOR", "leaf2"),
      seg(2000, 0, 2000, 900, "A-DOOR", "jamb1"),
      seg(2900, 0, 2900, 100, "A-DOOR", "jamb2"),
      seg(2000, 0, 2900, 900, "A-DOOR", "swing"),
      // longer swing arm must NOT win over leaf
      seg(2900, 0, 2900, 1200, "A-DOOR", "swingArm"),
    ];
    const traced = traceOpeningsFromCad(segments, { kind: "door" });
    assert.ok(traced.openings.length >= 1);
    const o = traced.openings[0];
    assert.ok(o.widthMm >= 600 && o.widthMm <= 1200);
    // Leaf center on wall y≈25, not swing mid
    assert.ok(Math.abs(o.centerMm.y) < 80, `centerY=${o.centerMm.y}`);
    assert.ok(Math.abs(o.centerMm.x - 2450) < 200);

    const walls: HostWall[] = [
      {
        id: 101,
        startMm: { x: 0, y: 0 },
        endMm: { x: 6000, y: 0 },
        lengthMm: 6000,
      },
      {
        id: 202,
        startMm: { x: 2900, y: 0 },
        endMm: { x: 2900, y: 5000 },
        lengthMm: 5000,
      },
    ];
    const planned = matchOpeningToHost(o, walls, { maxHostDistanceMm: 800 });
    assert.ok(planned);
    // Must pick horizontal wall (parallel to leaf), not vertical swing wall
    assert.equal(planned!.hostWallId, 101);
    assert.ok(planned!.hostDistanceMm < 80);
  });

  it("detects WC door leaves on corridor wall, not swing arms", () => {
    // Three WC doors: horizontal leaf 610 + vertical swing 690
    const segments = [
      ...[14825, 15925, 16940].flatMap((mx, i) => [
        seg(mx - 305, 6575, mx + 305, 6575, "A-DOOR", `h1-${i}`),
        seg(mx - 305, 6605, mx + 305, 6605, "A-DOOR", `h2-${i}`),
        seg(mx + 345, 5885, mx + 345, 6575, "A-DOOR", `v1-${i}`),
        seg(mx + 375, 5885, mx + 375, 6575, "A-DOOR", `v2-${i}`),
      ]),
    ];
    const traced = traceOpeningsFromCad(segments, { kind: "door" });
    assert.equal(traced.openings.length, 3);
    for (const o of traced.openings) {
      assert.ok(Math.abs(o.centerMm.y - 6590) < 30, `y=${o.centerMm.y}`);
      assert.ok(o.widthMm >= 580 && o.widthMm <= 650);
    }
    const walls: HostWall[] = [
      {
        id: 1,
        startMm: { x: 14000, y: 6590 },
        endMm: { x: 18000, y: 6590 },
        lengthMm: 4000,
      },
      {
        id: 2,
        startMm: { x: 17575, y: 4800 },
        endMm: { x: 17575, y: 6600 },
        lengthMm: 1800,
      },
    ];
    const { planned } = matchOpeningsToHosts(traced.openings, walls, {
      maxHostDistanceMm: 800,
    });
    assert.equal(planned.length, 3);
    assert.ok(planned.every((p) => p.hostWallId === 1));
  });

  it("does not place opening on T-junction preference (mid-span preferred)", () => {
    const opening = {
      kind: "door" as const,
      centerMm: { x: 3000, y: 50, z: 0 },
      widthMm: 900,
      sourceCadIds: ["a"],
      bboxMm: { minX: 2550, maxX: 3450, minY: -50, maxY: 900 },
      segmentCount: 3,
    };
    const walls: HostWall[] = [
      // long facade
      {
        id: 1,
        startMm: { x: 0, y: 0 },
        endMm: { x: 8000, y: 0 },
        lengthMm: 8000,
      },
      // short stub meeting at 3000 (T-junction)
      {
        id: 2,
        startMm: { x: 3000, y: 0 },
        endMm: { x: 3000, y: 2000 },
        lengthMm: 2000,
      },
    ];
    const planned = matchOpeningToHost(opening, walls, {
      maxHostDistanceMm: 800,
    });
    assert.ok(planned);
    // Should prefer long wall id=1 (mid-span), not the stub
    assert.equal(planned!.hostWallId, 1);
  });

  it("skips host when farther than maxHostDistanceMm", () => {
    const opening = {
      kind: "window" as const,
      centerMm: { x: 1000, y: 5000, z: 0 },
      widthMm: 1200,
      sourceCadIds: [],
      bboxMm: { minX: 400, maxX: 1600, minY: 4900, maxY: 5100 },
      segmentCount: 2,
    };
    const walls: HostWall[] = [
      {
        id: 5,
        startMm: { x: 0, y: 0 },
        endMm: { x: 5000, y: 0 },
        lengthMm: 5000,
      },
    ];
    const planned = matchOpeningToHost(opening, walls, {
      maxHostDistanceMm: 400,
    });
    assert.equal(planned, null);
  });

  it("dedupes nearby duplicate clusters", () => {
    const a = {
      kind: "door" as const,
      centerMm: { x: 1000, y: 0, z: 0 },
      widthMm: 900,
      sourceCadIds: ["1"],
      bboxMm: { minX: 550, maxX: 1450, minY: -50, maxY: 900 },
      segmentCount: 2,
    };
    const b = {
      kind: "door" as const,
      centerMm: { x: 1050, y: 20, z: 0 },
      widthMm: 910,
      sourceCadIds: ["2"],
      bboxMm: { minX: 600, maxX: 1500, minY: -30, maxY: 920 },
      segmentCount: 4,
    };
    const deduped = dedupeOpenings([a, b], 400);
    assert.equal(deduped.length, 1);
    assert.equal(deduped[0].segmentCount, 4);
  });

  it("parses and matches door type width from name", () => {
    assert.equal(parseOpeningTypeWidthMm("Дверь 910 мм"), 910);
    assert.equal(parseOpeningTypeWidthMm("ADSK_Дверь_900×2100"), 900);
    const types = [
      { typeId: 1, name: "Дверь 800×2100" },
      { typeId: 2, name: "Дверь 900×2100" },
      { typeId: 3, name: "Дверь 1200×2100" },
    ];
    const m = matchOpeningTypeByWidth(905, types, 80);
    assert.ok(m);
    assert.equal(m!.typeId, 2);
  });

  it("verify reports CAD leaf → placement deviation (REV-148)", () => {
    const walls: HostWall[] = [
      {
        id: 10,
        startMm: { x: 0, y: 0 },
        endMm: { x: 5000, y: 0 },
        lengthMm: 5000,
      },
    ];
    const planned = [
      {
        kind: "door" as const,
        centerMm: { x: 2000, y: 30, z: 0 },
        widthMm: 900,
        sourceCadIds: [],
        bboxMm: { minX: 1550, maxX: 2450, minY: -50, maxY: 900 },
        segmentCount: 1,
        hostWallId: 10,
        locationMm: { x: 2000, y: 0, z: 0 },
        hostDistanceMm: 30,
        paramT: 0.4,
      },
      {
        kind: "door" as const,
        centerMm: { x: 3000, y: 0, z: 0 },
        widthMm: 900,
        sourceCadIds: [],
        bboxMm: { minX: 2550, maxX: 3450, minY: -50, maxY: 50 },
        segmentCount: 1,
        hostWallId: 10,
        // snapped far along wall — must fail honest verify
        locationMm: { x: 3800, y: 0, z: 0 },
        hostDistanceMm: 0,
        paramT: 0.76,
      },
    ];
    const verify = verifyOpeningsAgainstHosts(planned, walls, 100);
    assert.equal(verify.items[0].ok, true);
    assert.ok(verify.items[0].deviationMm < 50);
    assert.equal(verify.items[1].ok, false);
    assert.ok(verify.items[1].deviationMm > 100);
    assert.equal(verify.failedCount, 1);
  });

  it("projectPointOntoWall clamps to segment ends", () => {
    const wall: HostWall = {
      id: 1,
      startMm: { x: 0, y: 0 },
      endMm: { x: 1000, y: 0 },
    };
    const beyond = projectPointOntoWall({ x: 1500, y: 50 }, wall);
    assert.equal(beyond.t, 1);
    assert.ok(Math.abs(beyond.point.x - 1000) < 0.1);
    assert.ok((beyond.alongOvershootMm ?? 0) > 400);
  });

  it("estimateOpeningWidthMm prefers door-like side", () => {
    const w = estimateOpeningWidthMm(
      { minX: 0, maxX: 900, minY: 0, maxY: 900 },
      600,
      2500
    );
    assert.ok(w >= 800 && w <= 1000);
  });

  it("window layer patterns exclude A-GLAZ-CURT via default exclude", () => {
    const segments = [
      seg(0, 0, 1500, 0, "A-GLAZ", "win"),
      seg(0, 100, 1500, 100, "A-GLAZ", "win2"),
      seg(0, 0, 4000, 0, "A-GLAZ-CURT", "curt"),
    ];
    const traced = traceOpeningsFromCad(segments, { kind: "window" });
    assert.ok(traced.openings.length >= 1);
    assert.ok(
      traced.openings.every((o) => (o.layer ?? "").toLowerCase() !== "a-glaz-curt")
    );
  });

  it("excludes A-GLAZ-CWMG curtain mullions from windows", () => {
    const segments = [
      seg(0, 0, 1200, 0, "WINDOW", "real"),
      seg(0, 50, 1200, 50, "WINDOW", "real2"),
      seg(0, 0, 8000, 0, "A-GLAZ-CWMG-MCUT", "mull"),
      seg(0, 100, 8000, 100, "A-GLAZ-CWMG-OTLN", "mull2"),
    ];
    const traced = traceOpeningsFromCad(segments, { kind: "window" });
    assert.equal(traced.stats.excluded, 2);
    assert.ok(traced.openings.length >= 1);
    assert.ok(
      traced.openings.every(
        (o) => !(o.layer ?? "").toLowerCase().includes("cwmg")
      )
    );
  });

  it("matchOpeningsToHosts splits planned vs unmatched", () => {
    const openings = [
      {
        kind: "door" as const,
        centerMm: { x: 1000, y: 10, z: 0 },
        widthMm: 900,
        sourceCadIds: [],
        bboxMm: { minX: 550, maxX: 1450, minY: -50, maxY: 900 },
        segmentCount: 2,
      },
      {
        kind: "door" as const,
        centerMm: { x: 1000, y: 9000, z: 0 },
        widthMm: 900,
        sourceCadIds: [],
        bboxMm: { minX: 550, maxX: 1450, minY: 8900, maxY: 9900 },
        segmentCount: 2,
      },
    ];
    const walls: HostWall[] = [
      {
        id: 7,
        startMm: { x: 0, y: 0 },
        endMm: { x: 4000, y: 0 },
        lengthMm: 4000,
      },
    ];
    const { planned, unmatched } = matchOpeningsToHosts(openings, walls, {
      maxHostDistanceMm: 500,
    });
    assert.equal(planned.length, 1);
    assert.equal(unmatched.length, 1);
  });

  it("clusterOpeningSegments groups connected jambs", () => {
    const segments = [
      seg(0, 0, 900, 0, "A-DOOR"),
      seg(900, 0, 900, 50, "A-DOOR"),
      seg(5000, 0, 5900, 0, "A-DOOR"),
    ];
    const clusters = clusterOpeningSegments(segments, 450);
    assert.equal(clusters.length, 2);
    const sizes = clusters.map((c) => c.length).sort();
    assert.deepEqual(sizes, [1, 2]);
  });

  it("openingsFromClusters skips tiny noise", () => {
    const tiny = [seg(0, 0, 40, 0, "A-DOOR")];
    const openings = openingsFromClusters([tiny], "door", {
      minOpeningWidthMm: 600,
    });
    assert.equal(openings.length, 0);
  });

  it("prefers MCUT leaf over OTLN swing (REV-148)", () => {
    const segments = [
      // True leaf on MCUT
      seg(1000, 0, 1610, 0, "A-DOOR-____-MCUT", "m1"),
      seg(1000, 30, 1610, 30, "A-DOOR-____-MCUT", "m2"),
      // Long OTLN swing arm — must not win
      seg(1610, 0, 1610, 900, "A-DOOR-____-OTLN", "swing"),
      seg(1000, -20, 1610, -20, "A-DOOR-____-OTLN", "otln"),
    ];
    const traced = traceOpeningsFromCad(segments, { kind: "door" });
    assert.ok(traced.openings.length >= 1);
    const o = traced.openings[0];
    assert.ok((o.layer ?? "").toLowerCase().includes("mcut"), o.layer);
    assert.ok(Math.abs(o.centerMm.y - 15) < 40, `y=${o.centerMm.y}`);
  });

  it("bridges gap between short stall walls (REV-148)", () => {
    const opening = {
      kind: "door" as const,
      centerMm: { x: 51968, y: 18571, z: 0 },
      widthMm: 610,
      layer: "A-DOOR-____-MCUT",
      sourceCadIds: ["a"],
      bboxMm: { minX: 51663, maxX: 52273, minY: 18550, maxY: 18590 },
      segmentCount: 2,
      leafDir: { x: 1, y: 0 },
    };
    const walls: HostWall[] = [
      {
        id: 1,
        startMm: { x: 51243, y: 18612 },
        endMm: { x: 51568, y: 18612 },
        lengthMm: 325,
      },
      {
        id: 2,
        startMm: { x: 52368, y: 18612 },
        endMm: { x: 52693, y: 18612 },
        lengthMm: 325,
      },
    ];
    const planned = matchOpeningToHost(opening, walls, {
      maxHostDistanceMm: 250,
      allowBridge: true,
    });
    assert.ok(planned, "expected gap bridge");
    assert.ok(planned!.bridge, "bridge required");
    assert.equal(planned!.bridge!.kind, "gap");
    assert.ok(Math.abs(planned!.locationMm.x - 51968) < 5);
    assert.ok(planned!.hostDistanceMm < 80);
    // Honest verify vs CAD
    const verify = verifyOpeningsAgainstHosts([planned!], walls, 100);
    assert.equal(verify.ok, true);
  });

  it("rejects far end-snap without bridge when allowBridge=false", () => {
    const opening = {
      kind: "door" as const,
      centerMm: { x: 51968, y: 18571, z: 0 },
      widthMm: 610,
      sourceCadIds: [],
      bboxMm: { minX: 51663, maxX: 52273, minY: 18550, maxY: 18590 },
      segmentCount: 2,
      leafDir: { x: 1, y: 0 },
    };
    const walls: HostWall[] = [
      {
        id: 1,
        startMm: { x: 51243, y: 18612 },
        endMm: { x: 51568, y: 18612 },
        lengthMm: 325,
      },
    ];
    const planned = matchOpeningToHost(opening, walls, {
      maxHostDistanceMm: 250,
      allowBridge: false,
    });
    assert.equal(planned, null);
  });
});
