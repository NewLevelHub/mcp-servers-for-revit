/**
 * REV-149: CAD column tracing — rectangular and round columns from DWG.
 *
 * Columns used to fall through every path: their hatch was dropped by the wall tracer
 * (hatch < 1500 mm) and their outline, if it survived, became a stub wall. They are point
 * elements with a size and a rotation, so they get their own detector.
 *
 * Pure geometry (mm); unit-tested without Revit.
 */

import type { CadSegment, CadBlock, PointMm, BboxMm } from "./cadWallTracing.js";

export const DEFAULT_COLUMN_LAYER_PATTERNS = [
  "s-col",
  "a-col",
  "column",
  "колонн",
  "s-grid-col",
];

/** Grids, dimensions and text share the S-* prefix but are not columns. */
export const DEFAULT_EXCLUDE_COLUMN_LAYERS = [
  "s-anno",
  "s-grid-iden",
  "grid-iden",
  "text",
  "dim",
];

export type ColumnShape = "rectangular" | "round";

export type DetectedColumn = {
  centerMm: PointMm;
  /** Size across the column's own X axis (mm). Diameter for round columns. */
  widthMm: number;
  /** Size across the column's own Y axis (mm). Equals widthMm for round columns. */
  depthMm: number;
  /** Rotation of the column X axis, degrees CCW, normalised to [0, 90). */
  rotationDeg: number;
  shape: ColumnShape;
  layer?: string;
  sourceCadIds: string[];
  segmentCount: number;
  bboxMm: BboxMm;
  blockName?: string;
};

export type ColumnTypeCandidate = {
  typeId: number;
  name: string;
  widthMm?: number;
  depthMm?: number;
};

export type TraceColumnsOptions = {
  layerPatterns?: string[];
  excludeLayers?: string[];
  minSizeMm?: number;
  maxSizeMm?: number;
  /** Reject elongated shapes — those are walls, not columns (default 3). */
  maxAspectRatio?: number;
  /** Cluster gap when the DWG has no block instances (default 120). */
  clusterGapMm?: number;
  /** Merge columns whose centres are closer than this (default 200). */
  dedupeDistanceMm?: number;
  bboxMm?: BboxMm;
  blocks?: CadBlock[];
};

function dist(ax: number, ay: number, bx: number, by: number): number {
  return Math.hypot(bx - ax, by - ay);
}

function segmentLength(seg: CadSegment): number {
  if (seg.lengthMm != null && seg.lengthMm > 0) return seg.lengthMm;
  return dist(seg.startMm.x, seg.startMm.y, seg.endMm.x, seg.endMm.y);
}

function layerMatches(layer: string, patterns: string[]): boolean {
  const lower = (layer ?? "").toLowerCase();
  if (!lower) return false;
  return patterns.some((p) => lower.includes(p.toLowerCase()));
}

function bboxOf(points: PointMm[]): BboxMm {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const p of points) {
    if (p.x < minX) minX = p.x;
    if (p.y < minY) minY = p.y;
    if (p.x > maxX) maxX = p.x;
    if (p.y > maxY) maxY = p.y;
  }
  return { minX, minY, maxX, maxY };
}

function segmentPoints(segs: CadSegment[]): PointMm[] {
  const out: PointMm[] = [];
  for (const s of segs) {
    out.push(s.startMm, s.endMm);
  }
  return out;
}

export function filterSegmentsForColumns(
  segments: CadSegment[],
  options: {
    layerPatterns?: string[];
    excludeLayers?: string[];
    bboxMm?: BboxMm;
  } = {}
): { segments: CadSegment[]; excluded: number } {
  const patterns = options.layerPatterns ?? DEFAULT_COLUMN_LAYER_PATTERNS;
  const exclude = options.excludeLayers ?? DEFAULT_EXCLUDE_COLUMN_LAYERS;
  const bbox = options.bboxMm;

  let excluded = 0;
  const kept: CadSegment[] = [];
  for (const seg of segments) {
    const layer = (seg.layer ?? "").toLowerCase();
    if (exclude.some((p) => layer.includes(p.toLowerCase()))) {
      excluded++;
      continue;
    }
    if (!layerMatches(layer, patterns)) {
      excluded++;
      continue;
    }
    if (bbox) {
      const xs = [seg.startMm.x, seg.endMm.x];
      const ys = [seg.startMm.y, seg.endMm.y];
      if (
        Math.max(...xs) < bbox.minX ||
        Math.min(...xs) > bbox.maxX ||
        Math.max(...ys) < bbox.minY ||
        Math.min(...ys) > bbox.maxY
      ) {
        excluded++;
        continue;
      }
    }
    kept.push(seg);
  }

  return { segments: kept, excluded };
}

/**
 * Group segments into column symbols.
 * Prefers DWG block instances (exact); falls back to proximity clustering.
 */
export function groupColumnSegments(
  segments: CadSegment[],
  clusterGapMm = 120
): CadSegment[][] {
  const byBlock = new Map<number, CadSegment[]>();
  const loose: CadSegment[] = [];
  for (const seg of segments) {
    if (seg.blockIndex != null && seg.blockIndex >= 0) {
      const list = byBlock.get(seg.blockIndex) ?? [];
      list.push(seg);
      byBlock.set(seg.blockIndex, list);
    } else {
      loose.push(seg);
    }
  }

  const groups = [...byBlock.values()];
  if (loose.length === 0) return groups;

  // Union-find on endpoint proximity for DWGs without block instances.
  const parent = loose.map((_, i) => i);
  const find = (i: number): number => {
    let r = i;
    while (parent[r] !== r) r = parent[r];
    let x = i;
    while (parent[x] !== x) {
      const next = parent[x];
      parent[x] = r;
      x = next;
    }
    return r;
  };
  const unite = (a: number, b: number) => {
    const ra = find(a);
    const rb = find(b);
    if (ra !== rb) parent[rb] = ra;
  };

  for (let i = 0; i < loose.length; i++) {
    for (let j = i + 1; j < loose.length; j++) {
      const a = loose[i];
      const b = loose[j];
      const pairs: Array<[PointMm, PointMm]> = [
        [a.startMm, b.startMm],
        [a.startMm, b.endMm],
        [a.endMm, b.startMm],
        [a.endMm, b.endMm],
      ];
      if (pairs.some(([p, q]) => dist(p.x, p.y, q.x, q.y) <= clusterGapMm)) {
        unite(i, j);
      }
    }
  }

  const looseGroups = new Map<number, CadSegment[]>();
  for (let i = 0; i < loose.length; i++) {
    const r = find(i);
    const list = looseGroups.get(r) ?? [];
    list.push(loose[i]);
    looseGroups.set(r, list);
  }

  return [...groups, ...looseGroups.values()];
}

/** Normalise a rectangle rotation: a square/rect repeats every 90°. */
export function normalizeColumnRotationDeg(deg: number): number {
  let d = deg % 90;
  if (d < 0) d += 90;
  // 89.99° is really 0° with float noise.
  if (d > 89.99) d = 0;
  return Math.round(d * 100) / 100;
}

/**
 * Turn one group of segments into a column.
 * Round columns come from arc metadata (a circle is one or more arcs sharing a centre);
 * rectangular ones are measured in the frame of their own longest edge, so rotated
 * columns keep their true size instead of an inflated axis-aligned bbox.
 */
export function columnFromSegments(
  segs: CadSegment[],
  options: {
    minSizeMm?: number;
    maxSizeMm?: number;
    maxAspectRatio?: number;
    blockName?: string;
  } = {}
): DetectedColumn | null {
  if (segs.length === 0) return null;
  const minSize = options.minSizeMm ?? 150;
  const maxSize = options.maxSizeMm ?? 1500;
  const maxAspect = options.maxAspectRatio ?? 3;

  const sourceCadIds = segs.map((s) => s.cadId ?? "").filter(Boolean);
  const layer = segs[0]?.layer;
  const bboxMm = bboxOf(segmentPoints(segs));

  // Round column: arcs sharing a centre.
  const arcSeg = segs.find((s) => s.arcCenterMm && s.arcRadiusMm);
  if (arcSeg?.arcCenterMm && arcSeg.arcRadiusMm) {
    const diameter = arcSeg.arcRadiusMm * 2;
    if (diameter >= minSize && diameter <= maxSize) {
      return {
        centerMm: { x: arcSeg.arcCenterMm.x, y: arcSeg.arcCenterMm.y, z: 0 },
        widthMm: Math.round(diameter),
        depthMm: Math.round(diameter),
        rotationDeg: 0,
        shape: "round",
        layer,
        sourceCadIds,
        segmentCount: segs.length,
        bboxMm,
        blockName: options.blockName,
      };
    }
    return null;
  }

  // Rectangular: measure along the longest edge.
  let dominant = { x: 1, y: 0 };
  let longest = 0;
  for (const s of segs) {
    const len = segmentLength(s);
    if (len <= longest) continue;
    const dx = s.endMm.x - s.startMm.x;
    const dy = s.endMm.y - s.startMm.y;
    const l = Math.hypot(dx, dy);
    if (l < 1e-6) continue;
    longest = len;
    dominant = { x: dx / l, y: dy / l };
  }

  const normal = { x: -dominant.y, y: dominant.x };
  const pts = segmentPoints(segs);
  let minU = Infinity;
  let maxU = -Infinity;
  let minV = Infinity;
  let maxV = -Infinity;
  for (const p of pts) {
    const u = p.x * dominant.x + p.y * dominant.y;
    const v = p.x * normal.x + p.y * normal.y;
    if (u < minU) minU = u;
    if (u > maxU) maxU = u;
    if (v < minV) minV = v;
    if (v > maxV) maxV = v;
  }

  const sizeU = maxU - minU;
  const sizeV = maxV - minV;
  if (sizeU < minSize || sizeV < minSize) return null;
  if (sizeU > maxSize || sizeV > maxSize) return null;

  const aspect = Math.max(sizeU, sizeV) / Math.max(1, Math.min(sizeU, sizeV));
  if (aspect > maxAspect) return null; // a long thin box is a wall, not a column

  const midU = (minU + maxU) / 2;
  const midV = (minV + maxV) / 2;
  const centerMm: PointMm = {
    x: midU * dominant.x + midV * normal.x,
    y: midU * dominant.y + midV * normal.y,
    z: 0,
  };

  return {
    centerMm,
    widthMm: Math.round(sizeU),
    depthMm: Math.round(sizeV),
    rotationDeg: normalizeColumnRotationDeg(
      (Math.atan2(dominant.y, dominant.x) * 180) / Math.PI
    ),
    shape: "rectangular",
    layer,
    sourceCadIds,
    segmentCount: segs.length,
    bboxMm,
    blockName: options.blockName,
  };
}

export function dedupeColumns(
  columns: DetectedColumn[],
  distanceMm = 200
): DetectedColumn[] {
  const kept: DetectedColumn[] = [];
  const used = new Set<number>();
  for (let i = 0; i < columns.length; i++) {
    if (used.has(i)) continue;
    let best = columns[i];
    for (let j = i + 1; j < columns.length; j++) {
      if (used.has(j)) continue;
      const d = dist(
        best.centerMm.x,
        best.centerMm.y,
        columns[j].centerMm.x,
        columns[j].centerMm.y
      );
      if (d <= distanceMm) {
        used.add(j);
        // Prefer the richer symbol (outline + hatch beats outline alone).
        if (columns[j].segmentCount > best.segmentCount) best = columns[j];
      }
    }
    kept.push(best);
  }
  return kept;
}

export function traceColumnsFromCad(
  segments: CadSegment[],
  options: TraceColumnsOptions = {}
): {
  columns: DetectedColumn[];
  stats: {
    inputSegments: number;
    filteredSegments: number;
    groups: number;
    columns: number;
    excluded: number;
    rejected: number;
  };
} {
  const filtered = filterSegmentsForColumns(segments, {
    layerPatterns: options.layerPatterns,
    excludeLayers: options.excludeLayers,
    bboxMm: options.bboxMm,
  });

  const blocksByIndex = new Map<number, CadBlock>();
  for (const b of options.blocks ?? []) blocksByIndex.set(b.index, b);

  const groups = groupColumnSegments(
    filtered.segments,
    options.clusterGapMm ?? 120
  );

  let rejected = 0;
  const columns: DetectedColumn[] = [];
  for (const group of groups) {
    const blockIndex = group[0]?.blockIndex;
    const blockName =
      blockIndex != null && blockIndex >= 0
        ? blocksByIndex.get(blockIndex)?.name
        : undefined;
    const col = columnFromSegments(group, {
      minSizeMm: options.minSizeMm,
      maxSizeMm: options.maxSizeMm,
      maxAspectRatio: options.maxAspectRatio,
      blockName,
    });
    if (col) columns.push(col);
    else rejected++;
  }

  const deduped = dedupeColumns(columns, options.dedupeDistanceMm ?? 200);

  return {
    columns: deduped,
    stats: {
      inputSegments: segments.length,
      filteredSegments: filtered.segments.length,
      groups: groups.length,
      columns: deduped.length,
      excluded: filtered.excluded,
      rejected,
    },
  };
}

/** Parse "400x400" / "Колонна 300х600" / "D400" from a type name. */
export function parseColumnTypeSizeMm(
  name: string
): { widthMm: number; depthMm: number } | null {
  if (!name) return null;

  const pair = name.match(/(\d{2,4})\s*[x×хX*]\s*(\d{2,4})/);
  if (pair) {
    const w = Number(pair[1]);
    const d = Number(pair[2]);
    if ([w, d].every((v) => Number.isFinite(v) && v >= 100 && v <= 3000)) {
      return { widthMm: w, depthMm: d };
    }
  }

  const round = name.match(/[dømØD]\s*(\d{2,4})/);
  if (round) {
    const v = Number(round[1]);
    if (Number.isFinite(v) && v >= 100 && v <= 3000) {
      return { widthMm: v, depthMm: v };
    }
  }

  return null;
}

export function matchColumnTypeBySize(
  widthMm: number,
  depthMm: number,
  types: ColumnTypeCandidate[],
  tolMm = 60
): ColumnTypeCandidate | null {
  let best: ColumnTypeCandidate | null = null;
  let bestDiff = Infinity;

  for (const t of types) {
    const size =
      t.widthMm != null && t.depthMm != null
        ? { widthMm: t.widthMm, depthMm: t.depthMm }
        : parseColumnTypeSizeMm(t.name);
    if (!size) continue;

    // A 300×600 type also fits a 600×300 column — it is the same section rotated.
    const direct =
      Math.abs(size.widthMm - widthMm) + Math.abs(size.depthMm - depthMm);
    const swapped =
      Math.abs(size.widthMm - depthMm) + Math.abs(size.depthMm - widthMm);
    const diff = Math.min(direct, swapped);

    if (diff < bestDiff && diff <= tolMm * 2) {
      bestDiff = diff;
      best = { ...t, widthMm: size.widthMm, depthMm: size.depthMm };
    }
  }

  return best;
}
