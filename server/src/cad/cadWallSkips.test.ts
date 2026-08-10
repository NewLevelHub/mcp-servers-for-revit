import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { CadSegment } from "./cadWallTracing.js";
import { traceWallAxesFromCad } from "./cadWallTracing.js";

function seg(
  x0: number,
  y0: number,
  x1: number,
  y1: number,
  layer = "A-WALL",
  cadId?: string
): CadSegment {
  return {
    startMm: { x: x0, y: y0, z: 0 },
    endMm: { x: x1, y: y1, z: 0 },
    lengthMm: Math.hypot(x1 - x0, y1 - y0),
    layer,
    cadId: cadId ?? `${layer}-${x0}-${y0}-${x1}-${y1}`,
  };
}

/** A wall drawn as two parallel faces `thickness` apart. */
function doubleLine(
  y: number,
  x0: number,
  x1: number,
  thickness: number,
  layer = "A-WALL"
): CadSegment[] {
  return [
    seg(x0, y, x1, y, layer, `${layer}-${y}-a`),
    seg(x0, y + thickness, x1, y + thickness, layer, `${layer}-${y}-b`),
  ];
}

describe("wall tracing skip diagnostics (REV-150)", () => {
  it("reports a thick wall dropped by maxPairGapMm, with the measured gap", () => {
    // Three 200 mm walls trace fine; the 380 mm one exceeds the 280 mm default and
    // used to vanish with nothing but a counter to show for it.
    const segments = [
      ...doubleLine(0, 0, 6000, 200),
      ...doubleLine(3000, 0, 6000, 200),
      ...doubleLine(6000, 0, 6000, 200),
      ...doubleLine(9000, 0, 6000, 380),
    ];

    const traced = traceWallAxesFromCad(segments, { dropThicknessOutliers: false });

    assert.equal(traced.axes.length, 3);
    const wide = traced.skipped.filter((s) => s.reason === "gapTooWide");
    assert.equal(wide.length, 2); // both faces of the 380 mm wall
    assert.equal(wide[0].nearestParallelGapMm, 380);
    // The coordinates must let the caller find it on the plan.
    assert.equal(wide[0].startMm.y, 9000);
    assert.ok(traced.hints.some((h) => h.includes("maxPairGapMm")));
    assert.ok(traced.hints.some((h) => h.includes("400")));
  });

  it("traces the same thick wall once maxPairGapMm is raised", () => {
    const segments = [
      ...doubleLine(0, 0, 6000, 200),
      ...doubleLine(9000, 0, 6000, 380),
    ];

    const traced = traceWallAxesFromCad(segments, {
      maxPairGapMm: 400,
      dropThicknessOutliers: false,
    });

    assert.equal(traced.axes.length, 2);
    assert.equal(traced.skipped.length, 0);
    assert.equal(traced.hints.length, 0);
    assert.deepEqual(
      traced.axes.map((a) => a.thicknessMm).sort((a, b) => (a ?? 0) - (b ?? 0)),
      [200, 380]
    );
  });

  it("flags a single-line wall as noParallel rather than dropping it silently", () => {
    const traced = traceWallAxesFromCad([
      ...doubleLine(0, 0, 6000, 200),
      seg(0, 9000, 6000, 9000, "A-WALL", "lonely"),
    ]);

    const none = traced.skipped.filter((s) => s.reason === "noParallel");
    assert.equal(none.length, 1);
    assert.equal(none[0].startMm.y, 9000);
    assert.ok(traced.hints.some((h) => h.includes("requirePair")));
  });

  it("distinguishes a too-narrow gap from a too-wide one", () => {
    // 20 mm apart — hatch or an outline, not two wall faces.
    const traced = traceWallAxesFromCad([
      ...doubleLine(0, 0, 6000, 200),
      ...doubleLine(9000, 0, 6000, 20),
    ]);

    const narrow = traced.skipped.filter((s) => s.reason === "gapTooNarrow");
    assert.equal(narrow.length, 2);
    assert.equal(narrow[0].nearestParallelGapMm, 20);
    assert.ok(traced.hints.some((h) => h.includes("minPairGapMm")));
  });

  it("stays silent when every line paired", () => {
    const traced = traceWallAxesFromCad([
      ...doubleLine(0, 0, 6000, 200),
      ...doubleLine(3000, 0, 6000, 200),
    ]);

    assert.equal(traced.axes.length, 2);
    assert.equal(traced.skipped.length, 0);
    assert.equal(traced.hints.length, 0);
  });

  it("keeps the skipped layer so a cross-layer pairing is visible", () => {
    const traced = traceWallAxesFromCad([
      ...doubleLine(0, 0, 6000, 200),
      seg(0, 9000, 6000, 9000, "A-WALL", "face"),
      // A dimension line 900 mm away is the nearest parallel neighbour.
      seg(0, 9900, 6000, 9900, "A-DIMS", "dim"),
    ]);

    const none = traced.skipped.find((s) => s.startMm.y === 9000);
    assert.ok(none);
    assert.equal(none!.reason, "gapTooWide");
    assert.equal(none!.nearestParallelLayer, "a-dims");
  });
});
