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

/** A horizontal wall run drawn as two parallel faces `thickness` apart. */
function run(
  y: number,
  x0: number,
  x1: number,
  thickness: number,
  tag: string,
  layer = "A-WALL"
): CadSegment[] {
  return [
    seg(x0, y, x1, y, layer, `${tag}-a`),
    seg(x0, y + thickness, x1, y + thickness, layer, `${tag}-b`),
  ];
}

describe("opening-gap merge (REV-152)", () => {
  it("joins a wall broken by a door opening into one continuous axis", () => {
    // How DWG draws it: wall, 900 mm hole for the door, wall. Revit needs the wall whole
    // and the door cut into it — otherwise the run arrives as two stubs and the opening
    // tool has to invent a bridge wall between them.
    const segments = [
      ...run(0, 0, 3000, 200, "left"),
      ...run(0, 3900, 8000, 200, "right"),
    ];

    const split = traceWallAxesFromCad(segments, { openingGapMm: 0 });
    assert.equal(split.axes.length, 2, "without openingGapMm the run stays split");

    const joined = traceWallAxesFromCad(segments, { openingGapMm: 2500 });
    assert.equal(joined.axes.length, 1);
    assert.equal(joined.stats.bridgedOpeningGaps, 1);

    const axis = joined.axes[0];
    const spanMm = Math.abs(axis.endMm.x - axis.startMm.x);
    assert.ok(
      Math.abs(spanMm - 8000) < 1,
      `expected the axis to span the whole run, got ${spanMm} mm`
    );
  });

  it("leaves a gap wider than openingGapMm alone", () => {
    // A genuine passage is not an opening in a wall — bridging it would model a wall
    // that the DWG does not draw.
    const segments = [
      ...run(0, 0, 3000, 200, "left"),
      ...run(0, 7000, 10000, 200, "right"),
    ];

    const traced = traceWallAxesFromCad(segments, { openingGapMm: 2500 });
    assert.equal(traced.axes.length, 2);
    assert.equal(traced.stats.bridgedOpeningGaps, 0);
  });

  it("does not bridge across a thickness change on the same centreline", () => {
    // A 200 mm partition continuing as a 380 mm wall, both centred on y=0: two walls,
    // two types. Sharing a centreline is what makes this the real test of the guard —
    // nothing but the thickness difference stands between them.
    const segments = [
      ...run(-100, 0, 3000, 200, "thin"),
      ...run(-190, 3900, 8000, 380, "thick"),
    ];

    const traced = traceWallAxesFromCad(segments, {
      openingGapMm: 2500,
      maxPairGapMm: 450, // let the 380 mm wall pair at all
      dropThicknessOutliers: false,
    });

    assert.equal(traced.axes.length, 2, "thin and thick stay separate walls");
    assert.equal(traced.stats.bridgedOpeningGaps, 0);
  });
});
