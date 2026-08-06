import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  filterSegmentsByBbox,
  filterSegmentsForWallTracing,
  filterThicknessOutliers,
  traceWallAxesFromCad,
  verifyAxesAgainstCad,
  clusterThicknesses,
  parseWallTypeThicknessMm,
  matchWallTypeByThickness,
  type CadSegment,
} from "./cadWallTracing.js";

function seg(
  x0: number,
  y0: number,
  x1: number,
  y1: number,
  cadId?: string,
  layer?: string,
  cadLinkName?: string
): CadSegment {
  const lengthMm = Math.hypot(x1 - x0, y1 - y0);
  return {
    startMm: { x: x0, y: y0, z: 0 },
    endMm: { x: x1, y: y1, z: 0 },
    cadId,
    lengthMm,
    layer: layer ?? "wall",
    cadLinkName,
  };
}

describe("cadWallTracing (REV-140)", () => {
  it("pairs parallel double lines into one centerline with thickness", () => {
    const segments = [
      seg(0, 100, 5000, 100, "a"),
      seg(0, 300, 5000, 300, "b"),
    ];
    const result = traceWallAxesFromCad(segments, {
      minPairGapMm: 50,
      maxPairGapMm: 500,
      minWallLengthMm: 300,
    });
    assert.equal(result.axes.length, 1);
    assert.equal(result.stats.pairedCount, 1);
    const axis = result.axes[0];
    assert.ok(Math.abs(axis.startMm.y - 200) < 5);
    assert.ok(Math.abs(axis.endMm.y - 200) < 5);
    assert.ok(axis.lengthMm > 4900);
    assert.ok(axis.thicknessMm != null && Math.abs(axis.thicknessMm - 200) < 1);
    assert.equal(result.thicknessClusters[0]?.thicknessMm, 200);
  });

  it("requirePair skips unpaired face lines", () => {
    const segments = [
      seg(0, 100, 5000, 100, "a"),
      seg(0, 300, 5000, 300, "b"),
      seg(0, 2000, 4000, 2000, "lonely"),
    ];
    const result = traceWallAxesFromCad(segments, {
      minPairGapMm: 50,
      maxPairGapMm: 500,
      requirePair: true,
    });
    assert.equal(result.axes.length, 1);
    assert.equal(result.stats.unpairedSkipped, 1);
  });

  it("merges collinear segments with small gap", () => {
    const segments = [
      seg(0, 200, 2000, 200, "a"),
      seg(2050, 200, 5000, 200, "b"),
    ];
    const result = traceWallAxesFromCad(segments, {
      pairingMode: "raw",
      mergeGapMm: 100,
      minWallLengthMm: 300,
    });
    assert.equal(result.axes.length, 1);
    assert.ok(result.axes[0].lengthMm > 4900);
  });

  it("filters segments by bbox", () => {
    const segments = [
      seg(1000, 1000, 2000, 1000),
      seg(9000, 9000, 10000, 9000),
    ];
    const filtered = filterSegmentsByBbox(segments, {
      minX: 500,
      maxX: 3000,
      minY: 500,
      maxY: 3000,
    });
    assert.equal(filtered.length, 1);
    assert.equal(filtered[0].startMm.x, 1000);
  });

  it("verify allows centerline at half-thickness from faces", () => {
    const segments = [
      seg(0, 0, 5000, 0, "outer"),
      seg(0, 300, 5000, 300, "inner"),
    ];
    const traced = traceWallAxesFromCad(segments, {
      minPairGapMm: 50,
      maxPairGapMm: 500,
      minWallLengthMm: 300,
    });
    // Default tol 50 would wrongly fail a 300 mm wall (face is 150 mm away).
    const verify = verifyAxesAgainstCad(traced.axes, segments, 50);
    assert.equal(verify.failedAxes.length, 0);
    assert.ok(verify.maxDeviationMm <= 160);
  });

  it("skips segments outside bbox in trace pipeline", () => {
    const segments = [
      seg(5984, 1539, 7154, 1539),
      seg(-5000, -5000, -4000, -4000),
    ];
    const result = traceWallAxesFromCad(segments, {
      bboxMm: { minX: 5984, maxX: 13984, minY: 1539, maxY: 7539 },
      minPairGapMm: 50,
      maxPairGapMm: 500,
      minWallLengthMm: 300,
      pairingMode: "raw",
    });
    assert.equal(result.stats.afterBbox, 1);
    assert.equal(result.axes.length, 1);
    assert.ok(result.axes[0].startMm.x > 5000);
  });

  it("excludes furniture blocks and short hatch symbols", () => {
    const segments = [
      seg(0, 0, 5000, 0, "wall-a", "f2", "marks section"),
      seg(0, 0, 846, 0, "door-sym", "hatch", "door 910"),
      seg(0, 0, 3000, 0, "wall-b", "f2", "dachny_dom"),
    ];
    const filtered = filterSegmentsForWallTracing(segments);
    assert.equal(filtered.segments.length, 1);
    assert.equal(filtered.segments[0].cadId, "wall-b");
  });

  it("orthoOnly drops diagonal door swings", () => {
    const segments = [
      seg(0, 0, 3000, 0, "wall"),
      seg(0, 0, 900, 900, "swing"),
    ];
    const filtered = filterSegmentsForWallTracing(segments, {
      orthoOnly: true,
      minLengthMm: 0,
    });
    assert.equal(filtered.segments.length, 1);
    assert.equal(filtered.segments[0].cadId, "wall");
  });

  it("parses and matches wall type thickness from name", () => {
    assert.equal(parseWallTypeThicknessMm("Типовой - 200мм"), 200);
    assert.equal(parseWallTypeThicknessMm("Внутренние - Перегородка (1 час) 79мм"), 79);
    const match = matchWallTypeByThickness(75, [
      { typeId: 1, name: "Типовой - 200мм" },
      { typeId: 2, name: "Внутренние - Перегородка (1 час) 79мм" },
      { typeId: 3, name: "Типовой - 300мм" },
    ]);
    assert.equal(match?.typeId, 2);
    assert.deepEqual(clusterThicknesses([74, 76, 75, 298, 302]), [
      { thicknessMm: 75, count: 3 },
      { thicknessMm: 300, count: 2 },
    ]);
  });

  it("pairs opposite-winding faces without mirroring through origin", () => {
    // One face L→R, other R→L — common in exploded DWG polylines.
    const segments = [
      seg(0, -100, 5000, -100, "a"),
      seg(5000, -300, 0, -300, "b"),
    ];
    const result = traceWallAxesFromCad(segments, {
      minPairGapMm: 50,
      maxPairGapMm: 500,
      requirePair: true,
    });
    assert.equal(result.axes.length, 1);
    const axis = result.axes[0];
    assert.ok(axis.startMm.y < 0 && axis.endMm.y < 0, "must stay in negative Y");
    assert.ok(Math.abs(axis.startMm.y + 200) < 5);
    assert.ok(Math.abs((axis.thicknessMm ?? 0) - 200) < 1);
  });

  it("prefers nearest face over farther dimension line", () => {
    const segments = [
      seg(0, 0, 4000, 0, "inner", "wall"),
      seg(0, 150, 4000, 150, "outer", "wall"),
      seg(0, 500, 4000, 500, "dim", "0"),
    ];
    const result = traceWallAxesFromCad(segments, {
      minPairGapMm: 50,
      maxPairGapMm: 500,
      requirePair: true,
      dropThicknessOutliers: false,
    });
    assert.equal(result.axes.length, 1);
    assert.ok(Math.abs((result.axes[0].thicknessMm ?? 0) - 150) < 1);
  });

  it("drops thick outliers vs dominant 150 mm cluster", () => {
    const axes = [
      { startMm: { x: 0, y: 0 }, endMm: { x: 3000, y: 0 }, lengthMm: 3000, sourceCadIds: [], thicknessMm: 150, paired: true },
      { startMm: { x: 0, y: 1 }, endMm: { x: 3000, y: 1 }, lengthMm: 3000, sourceCadIds: [], thicknessMm: 150, paired: true },
      { startMm: { x: 0, y: 2 }, endMm: { x: 3000, y: 2 }, lengthMm: 3000, sourceCadIds: [], thicknessMm: 145, paired: true },
      { startMm: { x: 0, y: 3 }, endMm: { x: 3000, y: 3 }, lengthMm: 3000, sourceCadIds: [], thicknessMm: 160, paired: true },
      { startMm: { x: 0, y: 4 }, endMm: { x: 3000, y: 4 }, lengthMm: 3000, sourceCadIds: [], thicknessMm: 380, paired: true },
      { startMm: { x: 0, y: 5 }, endMm: { x: 3000, y: 5 }, lengthMm: 3000, sourceCadIds: [], thicknessMm: 510, paired: true },
    ];
    const filtered = filterThicknessOutliers(axes);
    assert.equal(filtered.dropped.length, 2);
    assert.ok(filtered.axes.every((a) => (a.thicknessMm ?? 0) < 250));
  });
});
