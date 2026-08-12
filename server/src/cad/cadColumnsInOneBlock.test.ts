import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { CadSegment } from "./cadWallTracing.js";
import { groupColumnSegments, traceColumnsFromCad } from "./cadColumnTracing.js";

function seg(
  x0: number,
  y0: number,
  x1: number,
  y1: number,
  blockIndex?: number
): CadSegment {
  return {
    startMm: { x: x0, y: y0, z: 0 },
    endMm: { x: x1, y: y1, z: 0 },
    lengthMm: Math.hypot(x1 - x0, y1 - y0),
    layer: "S-COLS",
    cadId: `S-COLS-${x0}-${y0}-${x1}-${y1}`,
    blockIndex,
  };
}

/** A closed square column outline, as a Revit DWG export draws it. */
function square(
  cx: number,
  cy: number,
  size: number,
  blockIndex?: number
): CadSegment[] {
  const h = size / 2;
  return [
    seg(cx - h, cy - h, cx + h, cy - h, blockIndex),
    seg(cx + h, cy - h, cx + h, cy + h, blockIndex),
    seg(cx + h, cy + h, cx - h, cy + h, blockIndex),
    seg(cx - h, cy + h, cx - h, cy - h, blockIndex),
  ];
}

describe("columns inside a single DWG block (REV-154)", () => {
  it("separates columns that share one block index", () => {
    // «Проект1»: the whole drawing exports as one nested block, so every column carries
    // blockIndex 1. Treating a block as a column merged three 800 mm columns 4.7 m apart
    // into one group, whose extent then failed the size check — three columns became none.
    const segments = [
      ...square(-4635, 10261, 800, 1),
      ...square(3120, 11490, 800, 1),
      ...square(-834, 9114, 800, 1),
    ];

    const groups = groupColumnSegments(segments);
    assert.equal(groups.length, 3, "one group per column, not one per block");

    const traced = traceColumnsFromCad(segments, {});
    assert.equal(traced.columns.length, 3);
    for (const column of traced.columns) {
      assert.equal(Math.round(column.widthMm), 800);
      assert.equal(Math.round(column.depthMm), 800);
      assert.equal(column.shape, "rectangular");
    }
  });

  it("keeps touching columns from different blocks apart", () => {
    // The block id still carries meaning when each symbol is its own instance: two columns
    // close enough for the proximity pass must not merge if the DWG says they are separate.
    const segments = [
      ...square(0, 0, 400, 7),
      ...square(420, 0, 400, 8),
    ];

    const groups = groupColumnSegments(segments);
    assert.equal(groups.length, 2);
  });

  it("still clusters loose geometry that belongs to no block", () => {
    const segments = [...square(0, 0, 500), ...square(5000, 0, 500)];
    assert.equal(groupColumnSegments(segments).length, 2);
  });
});
