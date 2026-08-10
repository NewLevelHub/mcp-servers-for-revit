import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { CadSegment } from "./cadWallTracing.js";
import {
  columnFromSegments,
  dedupeColumns,
  filterSegmentsForColumns,
  groupColumnSegments,
  matchColumnTypeBySize,
  normalizeColumnRotationDeg,
  parseColumnTypeSizeMm,
  traceColumnsFromCad,
} from "./cadColumnTracing.js";

function seg(
  x0: number,
  y0: number,
  x1: number,
  y1: number,
  layer = "S-COLS",
  blockIndex?: number
): CadSegment {
  return {
    startMm: { x: x0, y: y0, z: 0 },
    endMm: { x: x1, y: y1, z: 0 },
    lengthMm: Math.hypot(x1 - x0, y1 - y0),
    layer,
    cadId: `${layer}-${x0}-${y0}-${x1}-${y1}`,
    blockIndex,
  };
}

/** Four sides of an axis-aligned rectangle. */
function rect(
  cx: number,
  cy: number,
  w: number,
  h: number,
  layer = "S-COLS",
  blockIndex?: number
): CadSegment[] {
  const x0 = cx - w / 2;
  const x1 = cx + w / 2;
  const y0 = cy - h / 2;
  const y1 = cy + h / 2;
  return [
    seg(x0, y0, x1, y0, layer, blockIndex),
    seg(x1, y0, x1, y1, layer, blockIndex),
    seg(x1, y1, x0, y1, layer, blockIndex),
    seg(x0, y1, x0, y0, layer, blockIndex),
  ];
}

/** Rectangle rotated by angleDeg about its centre. */
function rotatedRect(
  cx: number,
  cy: number,
  w: number,
  h: number,
  angleDeg: number
): CadSegment[] {
  const a = (angleDeg * Math.PI) / 180;
  const cos = Math.cos(a);
  const sin = Math.sin(a);
  const corners = [
    { x: -w / 2, y: -h / 2 },
    { x: w / 2, y: -h / 2 },
    { x: w / 2, y: h / 2 },
    { x: -w / 2, y: h / 2 },
  ].map((p) => ({ x: cx + p.x * cos - p.y * sin, y: cy + p.x * sin + p.y * cos }));

  return corners.map((p, i) => {
    const q = corners[(i + 1) % corners.length];
    return seg(p.x, p.y, q.x, q.y);
  });
}

describe("cadColumnTracing (REV-149)", () => {
  it("keeps column layers and drops annotation layers", () => {
    const { segments, excluded } = filterSegmentsForColumns([
      seg(0, 0, 400, 0, "S-COLS"),
      seg(0, 0, 400, 0, "A-WALL"),
      seg(0, 0, 400, 0, "S-GRID-IDEN"),
    ]);
    assert.equal(segments.length, 1);
    assert.equal(excluded, 2);
  });

  it("measures an axis-aligned square column", () => {
    const col = columnFromSegments(rect(2000, 3000, 400, 400));
    assert.ok(col);
    assert.equal(col!.shape, "rectangular");
    assert.equal(col!.widthMm, 400);
    assert.equal(col!.depthMm, 400);
    assert.equal(Math.round(col!.centerMm.x), 2000);
    assert.equal(Math.round(col!.centerMm.y), 3000);
    assert.equal(col!.rotationDeg, 0);
  });

  it("keeps the real section of a rotated column instead of its bbox", () => {
    // A 300×600 column at 30° has a much larger axis-aligned bbox.
    const col = columnFromSegments(rotatedRect(0, 0, 600, 300, 30));
    assert.ok(col);
    assert.equal(col!.widthMm, 600);
    assert.equal(col!.depthMm, 300);
    assert.equal(Math.round(col!.rotationDeg), 30);
    assert.ok(Math.abs(col!.centerMm.x) < 1);
    assert.ok(Math.abs(col!.centerMm.y) < 1);
  });

  it("reads a round column from arc metadata", () => {
    const circle: CadSegment = {
      startMm: { x: 200, y: 0, z: 0 },
      endMm: { x: -200, y: 0, z: 0 },
      lengthMm: 400,
      layer: "S-COLS",
      cadId: "circle",
      curveType: "arc",
      arcId: "c1",
      arcCenterMm: { x: 0, y: 0, z: 0 },
      arcRadiusMm: 200,
      arcStartAngleDeg: 0,
      arcEndAngleDeg: 180,
    };
    const col = columnFromSegments([circle]);
    assert.ok(col);
    assert.equal(col!.shape, "round");
    assert.equal(col!.widthMm, 400);
    assert.equal(col!.depthMm, 400);
  });

  it("rejects elongated boxes — those are walls", () => {
    // 200 × 3000 is a partition, not a column.
    assert.equal(columnFromSegments(rect(0, 0, 3000, 200)), null);
  });

  it("rejects sections outside the size band", () => {
    assert.equal(columnFromSegments(rect(0, 0, 80, 80)), null);
    assert.equal(columnFromSegments(rect(0, 0, 2500, 2500)), null);
  });

  it("groups by DWG block instance when available", () => {
    const segments = [
      ...rect(0, 0, 400, 400, "S-COLS", 3),
      ...rect(6000, 0, 400, 400, "S-COLS", 4),
    ];
    const groups = groupColumnSegments(segments);
    assert.equal(groups.length, 2);
    assert.equal(groups[0].length, 4);
  });

  it("clusters by proximity when the DWG has no blocks", () => {
    const segments = [...rect(0, 0, 400, 400), ...rect(6000, 0, 400, 400)];
    const groups = groupColumnSegments(segments);
    assert.equal(groups.length, 2);
  });

  it("traces two columns end to end and skips wall segments", () => {
    const traced = traceColumnsFromCad([
      ...rect(0, 0, 400, 400, "S-COLS", 1),
      ...rect(6000, 0, 500, 500, "S-COLS", 2),
      seg(0, 5000, 8000, 5000, "A-WALL"),
    ]);
    assert.equal(traced.columns.length, 2);
    assert.equal(traced.stats.excluded, 1);
    assert.deepEqual(
      traced.columns.map((c) => c.widthMm).sort((a, b) => a - b),
      [400, 500]
    );
  });

  it("merges the outline and hatch of one column", () => {
    const outline = rect(0, 0, 400, 400);
    const hatch = rect(0, 0, 380, 380);
    const merged = dedupeColumns([
      columnFromSegments(outline)!,
      columnFromSegments(hatch)!,
    ]);
    assert.equal(merged.length, 1);
  });

  it("normalises rotation to the 0–90 range", () => {
    assert.equal(normalizeColumnRotationDeg(0), 0);
    assert.equal(normalizeColumnRotationDeg(90), 0);
    assert.equal(normalizeColumnRotationDeg(135), 45);
    assert.equal(normalizeColumnRotationDeg(-30), 60);
  });

  it("parses section sizes out of type names", () => {
    assert.deepEqual(parseColumnTypeSizeMm("400x400"), { widthMm: 400, depthMm: 400 });
    assert.deepEqual(parseColumnTypeSizeMm("Колонна 300х600"), {
      widthMm: 300,
      depthMm: 600,
    });
    assert.deepEqual(parseColumnTypeSizeMm("Круглая D400"), {
      widthMm: 400,
      depthMm: 400,
    });
    assert.equal(parseColumnTypeSizeMm("Колонна базовая"), null);
  });

  it("matches a 300x600 type to a 600x300 column — same section rotated", () => {
    const types = [
      { typeId: 1, name: "Колонна 300х600" },
      { typeId: 2, name: "Колонна 400х400" },
    ];
    assert.equal(matchColumnTypeBySize(600, 300, types)?.typeId, 1);
    assert.equal(matchColumnTypeBySize(400, 400, types)?.typeId, 2);
    assert.equal(matchColumnTypeBySize(900, 900, types), null);
  });
});
