import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { CadSegment } from "./cadWallTracing.js";
import {
  extractArcFaces,
  traceArcWallAxesFromCad,
  type ArcTraceOptions,
} from "./cadArcWallTracing.js";

const CENTER = { x: -4534.8, y: 13911, z: 0 };

function arc(
  cadId: string,
  radiusMm: number,
  startAngleDeg: number,
  endAngleDeg: number
): CadSegment {
  const at = (deg: number) => ({
    x: CENTER.x + radiusMm * Math.cos((deg * Math.PI) / 180),
    y: CENTER.y + radiusMm * Math.sin((deg * Math.PI) / 180),
    z: 0,
  });
  return {
    startMm: at(startAngleDeg),
    endMm: at(endAngleDeg),
    layer: "A-WALL",
    cadId,
    arcId: cadId,
    curveType: "arc",
    arcCenterMm: CENTER,
    arcRadiusMm: radiusMm,
    arcStartAngleDeg: startAngleDeg,
    arcEndAngleDeg: endAngleDeg,
  };
}

/**
 * The curved wall of «Проект1 - План этажа - Уровень 1.dwg»: outer face R5000, inner face
 * R4800, sliced into arcs by two doorways. Angles are the values the DWG actually reports.
 */
function proekt1Arcs(): CadSegment[] {
  return [
    arc("146", 5000, -88.9, -77.5),
    arc("147", 4800, -88.8, -77.9),
    arc("148", 5000, -60.3, -52.5),
    arc("149", 4800, -59.9, -52.6),
    arc("128", 5000, -52.5, -47.8),
    arc("129", 4800, -52.6, -47.7),
    arc("150", 5000, -47.8, -38.9),
    arc("151", 4800, -47.7, -39.3),
    arc("152", 5000, -21.7, -14.5),
    arc("153", 4800, -21.3, -14.6),
    arc("127", 5000, -14.5, -9.9),
    arc("130", 4800, -14.6, -9.8),
    arc("154", 5000, -9.9, 0),
    arc("155", 4800, -9.8, 0),
  ];
}

const OPTS: ArcTraceOptions = {
  minPairGapMm: 55,
  maxPairGapMm: 280,
  minWallLengthMm: 300,
  centerToleranceMm: 50,
  mergeGapMm: 50,
};

describe("extractArcFaces (REV-154)", () => {
  it("ignores segments without arc metadata", () => {
    const faces = extractArcFaces([
      arc("a", 5000, 0, 30),
      {
        startMm: { x: 0, y: 0 },
        endMm: { x: 1000, y: 0 },
        layer: "A-WALL",
        cadId: "line",
      },
    ]);

    assert.equal(faces.length, 1);
    assert.equal(faces[0].radiusMm, 5000);
  });

  it("normalises a range crossing the -180/180 seam", () => {
    const faces = extractArcFaces([arc("seam", 5000, 170, -170)]);

    assert.ok(faces[0].endAngleDeg > faces[0].startAngleDeg);
    assert.ok(
      Math.abs(faces[0].endAngleDeg - faces[0].startAngleDeg - 20) < 1e-6
    );
  });

  it("keeps one face per arc when the read is tessellated", () => {
    const chord = arc("chord", 5000, -20, -10);
    const faces = extractArcFaces([chord, { ...chord, cadId: "chord-2" }]);

    assert.equal(faces.length, 1);
  });
});

describe("traceArcWallAxesFromCad (REV-154)", () => {
  it("pairs concentric faces into a centreline arc with the measured thickness", () => {
    const result = traceArcWallAxesFromCad(
      [arc("outer", 5000, -30, 0), arc("inner", 4800, -30, 0)],
      OPTS
    );

    assert.equal(result.axes.length, 1);
    const axis = result.axes[0];
    assert.ok(Math.abs(axis.radiusMm - 4900) < 1e-6);
    assert.ok(Math.abs(axis.thicknessMm - 200) < 1e-6);
    assert.ok(Math.abs(axis.centerMm.x - CENTER.x) < 1e-6);
    assert.ok(Math.abs(axis.lengthMm - (4900 * 30 * Math.PI) / 180) < 1e-3);
  });

  it("puts midMm on the arc, not on the chord", () => {
    const result = traceArcWallAxesFromCad(
      [arc("outer", 5000, -30, 0), arc("inner", 4800, -30, 0)],
      OPTS
    );

    const { midMm, centerMm, radiusMm } = result.axes[0];
    const r = Math.hypot(midMm.x - centerMm.x, midMm.y - centerMm.y);
    assert.ok(Math.abs(r - radiusMm) < 1e-6, `midMm sits at r=${r}`);
  });

  it("merges arcs that continue one another but leaves doorways open", () => {
    const result = traceArcWallAxesFromCad(proekt1Arcs(), OPTS);

    // Three runs: the two ~18° gaps are doorways and must not be bridged.
    assert.equal(result.axes.length, 3);
    const spans = result.axes
      .map((a) => [
        Math.round(a.startAngleDeg * 10) / 10,
        Math.round(a.endAngleDeg * 10) / 10,
      ])
      .sort((a, b) => a[0] - b[0]);
    assert.deepEqual(spans, [
      [-88.8, -77.9],
      [-59.9, -39.3],
      [-21.3, 0],
    ]);
    for (const axis of result.axes) {
      assert.ok(Math.abs(axis.thicknessMm - 200) < 0.5);
      assert.ok(Math.abs(axis.radiusMm - 4900) < 0.5);
    }
  });

  it("reports a face whose partner missed the gap band instead of dropping it", () => {
    const result = traceArcWallAxesFromCad(
      [arc("outer", 5000, -30, 0), arc("inner", 4500, -30, 0)],
      OPTS
    );

    assert.equal(result.axes.length, 0);
    assert.equal(result.skipped.length, 2);
    assert.equal(result.skipped[0].reason, "gapTooWide");
    assert.ok(
      Math.abs((result.skipped[0].nearestConcentricGapMm ?? 0) - 500) < 1e-6
    );
  });

  it("reports a single-line curved wall as unpaired", () => {
    const result = traceArcWallAxesFromCad([arc("lonely", 5000, -30, 0)], OPTS);

    assert.equal(result.axes.length, 0);
    assert.equal(result.skipped[0].reason, "noConcentricPartner");
  });

  it("does not pair arcs drawn around different centres", () => {
    const far = arc("far", 4800, -30, 0);
    far.arcCenterMm = { x: CENTER.x + 900, y: CENTER.y, z: 0 };

    const result = traceArcWallAxesFromCad(
      [arc("outer", 5000, -30, 0), far],
      OPTS
    );

    assert.equal(result.axes.length, 0);
  });

  it("drops a paired arc shorter than minWallLengthMm", () => {
    const result = traceArcWallAxesFromCad(
      [arc("outer", 5000, -1, 0), arc("inner", 4800, -1, 0)],
      OPTS
    );

    assert.equal(result.axes.length, 0);
    assert.equal(result.stats.shortSkipped, 1);
    assert.ok(result.skipped.some((s) => s.reason === "tooShort"));
  });

  it("honours bboxMm", () => {
    const result = traceArcWallAxesFromCad(proekt1Arcs(), {
      ...OPTS,
      bboxMm: { minX: 99000, maxX: 99999, minY: 99000, maxY: 99999 },
    });

    assert.equal(result.axes.length, 0);
  });
});
