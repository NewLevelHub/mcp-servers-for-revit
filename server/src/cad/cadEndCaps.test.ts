import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { CadSegment } from "./cadWallTracing.js";
import { traceWallAxesFromCad } from "./cadWallTracing.js";

function seg(
  x0: number,
  y0: number,
  x1: number,
  y1: number,
  layer: string,
  cadId: string
): CadSegment {
  return {
    startMm: { x: x0, y: y0, z: 0 },
    endMm: { x: x1, y: y1, z: 0 },
    lengthMm: Math.hypot(x1 - x0, y1 - y0),
    layer,
    cadId,
  };
}

/**
 * Coordinates lifted from «Проект1 - План этажа - Уровень 1.dwg», a Revit DWG export:
 * a 200 mm wall run along x = -5554 / -5754 broken by a door, with the outline closed
 * across the jamb at each side of the opening.
 */
function wallWithJambCaps(): CadSegment[] {
  return [
    // Outer and inner face, below the opening.
    seg(-5754, 5418, -5754, 6318, "A-WALL", "outer-lower"),
    seg(-5554, 5418, -5554, 6318, "A-WALL", "inner-lower"),
    // The jamb cap: 200 mm across the wall, joining the two faces.
    seg(-5554, 6318, -5754, 6318, "A-WALL", "cap-lower"),
    // Above the opening.
    seg(-5754, 7233, -5754, 8500, "A-WALL", "outer-upper"),
    seg(-5554, 7233, -5554, 8500, "A-WALL", "inner-upper"),
    seg(-5754, 7233, -5554, 7233, "A-WALL", "cap-upper"),
  ];
}

describe("outline end caps (REV-152)", () => {
  it("ignores jamb caps instead of reporting them as unpaired walls", () => {
    const traced = traceWallAxesFromCad(wallWithJambCaps(), {});

    assert.equal(traced.stats.endCapsIgnored, 2, "both jamb caps recognised");
    assert.equal(
      traced.skipped.length,
      0,
      `caps must not surface as skips, got ${JSON.stringify(traced.skipped.map((s) => s.reason))}`
    );
    assert.deepEqual(traced.hints, []);
    assert.equal(traced.axes.length, 2, "the two wall stretches still trace");
  });

  it("never suggests a maxPairGapMm that would pair walls across a room", () => {
    // The old behaviour: 27 caps on one flat came back as gapTooWide and the hint read
    // "these are thick walls, retry with maxPairGapMm: 1120" — which would have paired
    // opposite walls of a corridor into one 1.1 m slab.
    const traced = traceWallAxesFromCad(wallWithJambCaps(), {});
    for (const hint of traced.hints) {
      assert.ok(
        !/maxPairGapMm:\s*(\d+)/.test(hint) ||
          Number(/maxPairGapMm:\s*(\d+)/.exec(hint)![1]) < 500,
        `hint would widen pairing dangerously: ${hint}`
      );
    }
  });

  it("reads thin curtain glazing as glazing, not as hatching", () => {
    // A-GLAZ-CURT draws the витраж as a closed 25 mm rectangle. minPairGapMm defaults to
    // 55, so the whole layer traced to zero axes and the hint blamed the layer.
    const glazing: CadSegment[] = [
      seg(-2229, 5668, -2229, 6759, "A-GLAZ-CURT", "face-a"),
      seg(-2204, 5668, -2204, 6759, "A-GLAZ-CURT", "face-b"),
      seg(-2229, 5668, -2204, 5668, "A-GLAZ-CURT", "cap-a"),
      seg(-2229, 6759, -2204, 6759, "A-GLAZ-CURT", "cap-b"),
    ];

    const rejected = traceWallAxesFromCad(glazing, {});
    assert.equal(rejected.axes.length, 0);
    assert.ok(
      rejected.hints.some((h) => /витраж/i.test(h) && /minPairGapMm:\s*20/.test(h)),
      `expected a glazing hint naming the gap, got ${JSON.stringify(rejected.hints)}`
    );

    // Following that hint has to actually work.
    const traced = traceWallAxesFromCad(glazing, { minPairGapMm: 20 });
    assert.equal(traced.axes.length, 1);
    assert.ok(Math.abs(traced.axes[0].lengthMm - 1091) < 5);
    assert.ok(Math.abs((traced.axes[0].thicknessMm ?? 0) - 25) < 1);
  });
});
