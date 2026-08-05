import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  filterSegmentsByBbox,
  traceWallAxesFromCad,
  verifyAxesAgainstCad,
  type CadSegment,
} from "./cadWallTracing.js";

function seg(
  x0: number,
  y0: number,
  x1: number,
  y1: number,
  cadId?: string
): CadSegment {
  const lengthMm = Math.hypot(x1 - x0, y1 - y0);
  return {
    startMm: { x: x0, y: y0, z: 0 },
    endMm: { x: x1, y: y1, z: 0 },
    cadId,
    lengthMm,
    layer: "wall",
  };
}

describe("cadWallTracing (REV-140)", () => {
  it("pairs parallel double lines into one centerline", () => {
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

  it("verify reports low deviation for centerline on double lines", () => {
    const segments = [
      seg(5984, 1539, 7154, 1539, "bottom-outer"),
      seg(5984, 1739, 7154, 1739, "bottom-inner"),
    ];
    const traced = traceWallAxesFromCad(segments, {
      minPairGapMm: 50,
      maxPairGapMm: 500,
      minWallLengthMm: 300,
    });
    const verify = verifyAxesAgainstCad(traced.axes, segments, 100);
    assert.ok(verify.maxDeviationMm <= 100);
    assert.equal(verify.failedAxes.length, 0);
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
});
