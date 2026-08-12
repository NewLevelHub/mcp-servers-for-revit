import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { CadSegment } from "./cadWallTracing.js";
import { traceWallBandsFromCad } from "./cadBandWallTracing.js";

function hLine(y: number, x0: number, x1: number, cadId = "s"): CadSegment {
  return {
    startMm: { x: x0, y, z: 0 },
    endMm: { x: x1, y, z: 0 },
    lengthMm: Math.abs(x1 - x0),
    layer: "A-WALL-____-MCUT",
    cadId,
  };
}

function vLine(x: number, y0: number, y1: number, cadId = "s"): CadSegment {
  return {
    startMm: { x, y: y0, z: 0 },
    endMm: { x, y: y1, z: 0 },
    lengthMm: Math.abs(y1 - y0),
    layer: "A-WALL-____-MCUT",
    cadId,
  };
}

describe("traceWallBandsFromCad", () => {
  it("measures a plain double-line wall at its real thickness", () => {
    const r = traceWallBandsFromCad([hLine(0, 0, 6000), hLine(200, 0, 6000)]);

    assert.equal(r.axes.length, 1);
    assert.equal(r.axes[0].thicknessMm, 200);
    assert.equal(r.axes[0].startMm.y, 100); // centreline
    assert.equal(r.axes[0].lengthMm, 6000);
  });

  it("measures a multi-layer partition across its faces, not between finish layers", () => {
    // «двг2.dwg»: a 125 mm partition drawn as 0/12.5/25 — core 75 — 100/112.5/125.
    // Nearest-neighbour pairing called this 12.5 mm (or 62.5, or 93.8).
    const ys = [0, 12.5, 25, 100, 112.5, 125];
    const r = traceWallBandsFromCad(ys.map((y, i) => hLine(y, 0, 5000, `l${i}`)));

    assert.equal(r.axes.length, 1);
    assert.equal(r.axes[0].thicknessMm, 125);
    assert.equal(r.axes[0].startMm.y, 62.5);
  });

  it("does not bridge two walls across the room between them", () => {
    // Two 200 mm walls with a 2 m room between: 0/200 ... 2200/2400.
    const r = traceWallBandsFromCad([
      hLine(0, 0, 8000),
      hLine(200, 0, 8000),
      hLine(2200, 0, 8000),
      hLine(2400, 0, 8000),
    ]);

    assert.equal(r.axes.length, 2);
    assert.deepEqual(
      r.axes.map((a) => a.thicknessMm).sort(),
      [200, 200]
    );
  });

  it("rejects a pair whose core is crossed by a third face", () => {
    // 0 ... 150 ... 300 must not also yield one 300 mm wall on top of the two 150 mm ones.
    const r = traceWallBandsFromCad(
      [hLine(0, 0, 6000), hLine(150, 0, 6000), hLine(300, 0, 6000)],
      { maxCoreMm: 320 }
    );

    assert.ok(
      !r.axes.some((a) => a.thicknessMm === 300),
      "a wall must not span another wall's face"
    );
    assert.ok(r.stats.coreBlocked > 0);
  });

  it("keeps lines apart when they only share a coordinate, not a stretch of plan", () => {
    // Same y, opposite ends of the building — not one wall.
    const r = traceWallBandsFromCad([
      hLine(0, 0, 5000),
      hLine(200, 0, 5000),
      hLine(0, 90000, 95000),
    ]);

    assert.equal(r.axes.length, 1);
    assert.equal(r.axes[0].endMm.x, 5000);
  });

  it("runs the wall only where both faces are drawn", () => {
    // Outer face full length, inner face stops at 3000 — the wall stops there too.
    const r = traceWallBandsFromCad(
      [hLine(0, 0, 9000), hLine(200, 0, 3000)],
      { bridgeGapMm: 0 }
    );

    assert.equal(r.axes.length, 1);
    assert.equal(r.axes[0].lengthMm, 3000);
  });

  it("joins a run broken by a door so the opening keeps its host", () => {
    const r = traceWallBandsFromCad(
      [
        hLine(0, 0, 4000),
        hLine(200, 0, 4000),
        hLine(0, 5000, 9000),
        hLine(200, 5000, 9000),
      ],
      { bridgeGapMm: 2600 }
    );

    assert.equal(r.axes.length, 1, "a 1 m door gap must not split the wall");
    assert.equal(r.axes[0].lengthMm, 9000);
  });

  it("traces vertical walls the same way", () => {
    const r = traceWallBandsFromCad([vLine(0, 0, 7000), vLine(250, 0, 7000)]);

    assert.equal(r.axes.length, 1);
    assert.equal(r.axes[0].thicknessMm, 250);
    assert.equal(r.axes[0].startMm.x, 125);
  });

  it("ignores bands outside the accepted thickness range", () => {
    const r = traceWallBandsFromCad([hLine(0, 0, 6000), hLine(25, 0, 6000)]);

    assert.equal(r.axes.length, 0, "25 mm is glazing, not a wall");
  });

  it("counts skewed segments instead of tracing them", () => {
    const r = traceWallBandsFromCad([
      hLine(0, 0, 6000),
      hLine(200, 0, 6000),
      {
        startMm: { x: 0, y: 0, z: 0 },
        endMm: { x: 3000, y: 3000, z: 0 },
        lengthMm: 4243,
        layer: "A-WALL-____-MCUT",
        cadId: "diag",
      },
    ]);

    assert.equal(r.stats.skewed, 1);
    assert.equal(r.axes.length, 1);
  });

  it("snaps a measured thickness onto the drawing's own set", () => {
    const r = traceWallBandsFromCad([hLine(0, 0, 6000), hLine(287.5, 0, 6000)]);

    assert.equal(r.axes[0].thicknessMm, 300);
  });

  it("reports thickness clusters ordered by how much wall they account for", () => {
    const r = traceWallBandsFromCad([
      hLine(0, 0, 20000),
      hLine(250, 0, 20000),
      hLine(5000, 0, 3000),
      hLine(5100, 0, 3000),
    ]);

    assert.equal(r.thicknessClusters[0].thicknessMm, 250);
    assert.equal(r.thicknessClusters[0].totalLengthMm, 20000);
  });
});
