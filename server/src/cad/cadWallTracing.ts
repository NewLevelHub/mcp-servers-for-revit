/**
 * REV-140: CAD wall tracing — bbox filter, double-line centerlines, collinear merge.
 * Pure geometry (mm); unit-tested without Revit.
 */

export type PointMm = { x: number; y: number; z?: number };

export type CadSegment = {
  startMm: PointMm;
  endMm: PointMm;
  layer?: string;
  cadId?: string;
  lengthMm?: number;
  curveType?: string;
  cadLinkName?: string;
  source?: string;
};

/** Substrings matched against cadLinkName (case-insensitive) — exploded furniture/door blocks. */
export const DEFAULT_EXCLUDE_CAD_LINK_PATTERNS = [
  "chair",
  "lamp (pl)",
  "lamp",
  "plant",
  "plants2",
  "armchaer",
  "ergwerg564",
  "ergwerg",
  "vu-",
  "piple",
  "_archtick",
  "marks section",
  "900x1300",
  "door 910",
  "door910",
  "f standard lamp",
];

export type WallTracingFilterOptions = {
  excludeCadLinkPatterns?: string[];
  excludeLayers?: string[];
  /** Skip hatch-layer segments shorter than this (door symbols ~846 mm). */
  hatchMinLengthMm?: number;
  minLengthMm?: number;
  bboxMm?: BboxMm;
  /** Keep only axis-aligned (±tolDeg) segments — drops door swings / diagonals. */
  orthoOnly?: boolean;
  orthoTolDeg?: number;
};

function segmentLength(seg: CadSegment): number {
  if (seg.lengthMm != null && seg.lengthMm > 0) return seg.lengthMm;
  return dist(seg.startMm.x, seg.startMm.y, seg.endMm.x, seg.endMm.y);
}

function matchesPattern(name: string, patterns: string[]): boolean {
  const lower = name.toLowerCase();
  return patterns.some((p) => lower.includes(p.toLowerCase()));
}

function isOrthogonal(seg: CadSegment, tolDeg: number): boolean {
  const dx = seg.endMm.x - seg.startMm.x;
  const dy = seg.endMm.y - seg.startMm.y;
  const len = Math.hypot(dx, dy);
  if (len < 0.1) return false;
  const angle = Math.abs(Math.atan2(dy, dx));
  const deg = (angle * 180) / Math.PI;
  const mod = ((deg % 90) + 90) % 90;
  return mod <= tolDeg || mod >= 90 - tolDeg;
}

export function computeSegmentsBbox(
  segments: CadSegment[],
  marginMm = 0
): BboxMm | undefined {
  if (segments.length === 0) return undefined;
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const seg of segments) {
    for (const p of [seg.startMm, seg.endMm]) {
      if (p.x < minX) minX = p.x;
      if (p.y < minY) minY = p.y;
      if (p.x > maxX) maxX = p.x;
      if (p.y > maxY) maxY = p.y;
    }
  }
  if (!Number.isFinite(minX)) return undefined;
  return {
    minX: minX - marginMm,
    minY: minY - marginMm,
    maxX: maxX + marginMm,
    maxY: maxY + marginMm,
  };
}

/**
 * Drop furniture/door exploded blocks and symbolic hatch rectangles before tracing.
 */
export function filterSegmentsForWallTracing(
  segments: CadSegment[],
  options?: WallTracingFilterOptions
): { segments: CadSegment[]; stats: { input: number; excluded: number } } {
  const patterns =
    options?.excludeCadLinkPatterns ?? DEFAULT_EXCLUDE_CAD_LINK_PATTERNS;
  const excludeLayers = options?.excludeLayers ?? [];
  const hatchMin = options?.hatchMinLengthMm ?? 1500;
  const minLen = options?.minLengthMm ?? 0;
  const bbox = options?.bboxMm;
  const orthoOnly = options?.orthoOnly ?? false;
  const orthoTol = options?.orthoTolDeg ?? 3;

  let excluded = 0;
  const kept: CadSegment[] = [];

  for (const seg of segments) {
    const linkName = seg.cadLinkName ?? "";
    if (linkName && matchesPattern(linkName, patterns)) {
      excluded++;
      continue;
    }

    const layer = (seg.layer ?? "").toLowerCase();
    if (
      excludeLayers.some(
        (l) => layer.includes(l.toLowerCase()) || layer === l.toLowerCase()
      )
    ) {
      excluded++;
      continue;
    }

    if (layer === "hatch" && segmentLength(seg) < hatchMin) {
      excluded++;
      continue;
    }

    if (minLen > 0 && segmentLength(seg) < minLen) {
      excluded++;
      continue;
    }

    if (orthoOnly && !isOrthogonal(seg, orthoTol)) {
      excluded++;
      continue;
    }

    if (bbox && !segmentIntersectsBbox(seg, bbox)) {
      excluded++;
      continue;
    }

    kept.push(seg);
  }

  return { segments: kept, stats: { input: segments.length, excluded } };
}

export type BboxMm = {
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
};

export type WallAxis = {
  startMm: PointMm;
  endMm: PointMm;
  lengthMm: number;
  sourceCadIds: string[];
  /** Measured gap between paired CAD faces (mm). */
  thicknessMm?: number;
  paired?: boolean;
};

export type TraceOptions = {
  toleranceMm?: number;
  mergeGapMm?: number;
  minPairGapMm?: number;
  maxPairGapMm?: number;
  minWallLengthMm?: number;
  bboxMm?: BboxMm;
  pairingMode?: "centerline" | "raw";
  /**
   * centerline mode: if true, skip unpaired face lines (prevents double walls
   * offset to each CAD face). Default true.
   */
  requirePair?: boolean;
  /**
   * Drop paired axes whose thickness is an outlier vs the dominant cluster
   * (e.g. wall face paired with a dimension line 400 mm away). Default true.
   */
  dropThicknessOutliers?: boolean;
  /** Max ratio vs primary cluster thickness before an axis is an outlier. */
  maxThicknessRatio?: number;
};

export type TraceStats = {
  inputSegments: number;
  afterBbox: number;
  afterPairing: number;
  afterMerge: number;
  pairedCount: number;
  unpairedSkipped: number;
  skippedShort: number;
  thicknessOutliersDropped: number;
};

export type TraceResult = {
  axes: WallAxis[];
  stats: TraceStats;
  thicknessClusters: ThicknessCluster[];
};

type InternalSeg = {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  cadId: string;
  layer: string;
  angle: number;
  offset: number;
  tMin: number;
  tMax: number;
  length: number;
  used: boolean;
};

const DEG = Math.PI / 180;

function round1(v: number): number {
  return Math.round(v * 10) / 10;
}

function dist(x0: number, y0: number, x1: number, y1: number): number {
  return Math.hypot(x1 - x0, y1 - y0);
}

function normalizeAngle(angle: number): number {
  let a = angle % Math.PI;
  if (a < 0) a += Math.PI;
  return a;
}

function anglesMatch(a: number, b: number, tolDeg: number): boolean {
  const d = Math.abs(a - b);
  return d <= tolDeg * DEG || Math.abs(d - Math.PI) <= tolDeg * DEG;
}

function offsetsMatch(a: number, b: number, tol: number): boolean {
  return Math.abs(a - b) <= tol;
}

function segmentMid(seg: CadSegment): { x: number; y: number } {
  return {
    x: (seg.startMm.x + seg.endMm.x) / 2,
    y: (seg.startMm.y + seg.endMm.y) / 2,
  };
}

function segmentIntersectsBbox(seg: CadSegment, bbox: BboxMm): boolean {
  const { minX, maxX, minY, maxY } = bbox;
  const pts = [seg.startMm, seg.endMm, segmentMid(seg)];
  for (const p of pts) {
    if (p.x >= minX && p.x <= maxX && p.y >= minY && p.y <= maxY) return true;
  }
  const x0 = seg.startMm.x;
  const y0 = seg.startMm.y;
  const x1 = seg.endMm.x;
  const y1 = seg.endMm.y;
  const tVals = [0, 1];
  const dx = x1 - x0;
  const dy = y1 - y0;
  if (Math.abs(dx) > 1e-6) {
    tVals.push((minX - x0) / dx, (maxX - x0) / dx);
  }
  if (Math.abs(dy) > 1e-6) {
    tVals.push((minY - y0) / dy, (maxY - y0) / dy);
  }
  for (const t of tVals) {
    if (t < 0 || t > 1) continue;
    const x = x0 + t * dx;
    const y = y0 + t * dy;
    if (x >= minX - 1 && x <= maxX + 1 && y >= minY - 1 && y <= maxY + 1) return true;
  }
  return false;
}

export function filterSegmentsByBbox(
  segments: CadSegment[],
  bbox?: BboxMm
): CadSegment[] {
  if (!bbox) return segments;
  return segments.filter((s) => segmentIntersectsBbox(s, bbox));
}

function toInternal(seg: CadSegment, index: number): InternalSeg | null {
  const x0 = seg.startMm.x;
  const y0 = seg.startMm.y;
  const x1 = seg.endMm.x;
  const y1 = seg.endMm.y;
  const length = dist(x0, y0, x1, y1);
  if (length < 0.1) return null;

  // Canonical frame from undirected angle — otherwise L→R vs R→L flips the
  // normal and offset sign, so double-line pairs miss or invent mirrored walls.
  const angle = normalizeAngle(Math.atan2(y1 - y0, x1 - x0));
  const dx = Math.cos(angle);
  const dy = Math.sin(angle);
  const nx = -dy;
  const ny = dx;
  const mx = (x0 + x1) / 2;
  const my = (y0 + y1) / 2;
  const offset = mx * nx + my * ny;
  const t0 = x0 * dx + y0 * dy;
  const t1 = x1 * dx + y1 * dy;
  const tMin = Math.min(t0, t1);
  const tMax = Math.max(t0, t1);

  return {
    x0,
    y0,
    x1,
    y1,
    cadId: seg.cadId ?? `seg${index}`,
    layer: (seg.layer ?? "").toLowerCase(),
    angle,
    offset,
    tMin,
    tMax,
    length,
    used: false,
  };
}

function internalToAxis(seg: InternalSeg, cadIds: string[]): WallAxis {
  return {
    startMm: { x: round1(seg.x0), y: round1(seg.y0), z: 0 },
    endMm: { x: round1(seg.x1), y: round1(seg.y1), z: 0 },
    lengthMm: round1(seg.length),
    sourceCadIds: cadIds,
    paired: false,
  };
}

function axisFromParams(
  angle: number,
  offset: number,
  tMin: number,
  tMax: number,
  cadIds: string[],
  thicknessMm?: number,
  paired?: boolean
): WallAxis {
  const dx = Math.cos(angle);
  const dy = Math.sin(angle);
  const nx = -dy;
  const ny = dx;
  const x0 = dx * tMin + nx * offset;
  const y0 = dy * tMin + ny * offset;
  const x1 = dx * tMax + nx * offset;
  const y1 = dy * tMax + ny * offset;
  return {
    startMm: { x: round1(x0), y: round1(y0), z: 0 },
    endMm: { x: round1(x1), y: round1(y1), z: 0 },
    lengthMm: round1(dist(x0, y0, x1, y1)),
    sourceCadIds: cadIds,
    thicknessMm: thicknessMm != null ? round1(thicknessMm) : undefined,
    paired,
  };
}

function overlapT(a: InternalSeg, b: InternalSeg): number {
  const start = Math.max(a.tMin, b.tMin);
  const end = Math.min(a.tMax, b.tMax);
  return Math.max(0, end - start);
}

function pairToCenterline(a: InternalSeg, b: InternalSeg): WallAxis {
  const angle = (a.angle + b.angle) / 2;
  const offset = (a.offset + b.offset) / 2;
  // Overlap along length keeps openings as gaps (union would bridge doors).
  const tMin = Math.max(a.tMin, b.tMin);
  const tMax = Math.min(a.tMax, b.tMax);
  const thickness = Math.abs(b.offset - a.offset);
  return axisFromParams(angle, offset, tMin, tMax, [a.cadId, b.cadId], thickness, true);
}

function pairDoubleLines(
  segments: InternalSeg[],
  opts: Required<
    Pick<
      TraceOptions,
      "minPairGapMm" | "maxPairGapMm" | "minWallLengthMm" | "toleranceMm" | "requirePair"
    >
  >
): { axes: WallAxis[]; pairedCount: number; unpairedSkipped: number } {
  const axes: WallAxis[] = [];
  let pairedCount = 0;
  let unpairedSkipped = 0;
  const angleTolDeg = 3;

  // Sort by offset within continuous angle neighborhoods (search adjacent degree bins).
  const sorted = [...segments].sort((a, b) => a.angle - b.angle || a.offset - b.offset);

  for (let i = 0; i < sorted.length; i++) {
    const a = sorted[i];
    if (a.used) continue;

    let bestJ = -1;
    let bestGap = Infinity;
    let bestOv = -1;
    let bestSameLayer = false;

    const consider = (j: number) => {
      const b = sorted[j];
      if (b.used) return;
      if (!anglesMatch(a.angle, b.angle, angleTolDeg)) return;

      const gap = Math.abs(b.offset - a.offset);
      if (gap < opts.minPairGapMm || gap > opts.maxPairGapMm) return;

      const ov = overlapT(a, b);
      const minLen = Math.min(a.length, b.length);
      if (ov < minLen * 0.25 && ov < 200) return;

      const sameLayer =
        a.layer.length > 0 && b.layer.length > 0 && a.layer === b.layer;

      // Prefer nearest face (real wall thickness). Same-layer beats cross-layer
      // at equal gap — avoids pairing a wall face with a dimension line.
      const betterGap = gap < bestGap - 0.5;
      const equalGap = Math.abs(gap - bestGap) <= 0.5;
      if (
        betterGap ||
        (equalGap && sameLayer && !bestSameLayer) ||
        (equalGap && sameLayer === bestSameLayer && ov > bestOv)
      ) {
        bestGap = gap;
        bestOv = ov;
        bestJ = j;
        bestSameLayer = sameLayer;
      }
    };

    for (let j = i + 1; j < sorted.length; j++) {
      const b = sorted[j];
      if (!anglesMatch(a.angle, b.angle, angleTolDeg)) {
        if (b.angle - a.angle > angleTolDeg * DEG + 0.01) break;
        continue;
      }
      // Nearest-first: once gap exceeds best+margin and offsets only grow, stop.
      const gap = Math.abs(b.offset - a.offset);
      if (bestJ >= 0 && gap > bestGap + 30 && b.offset > a.offset) {
        // still allow slightly farther same-layer? no — nearest wins
        if (gap > bestGap + 80) break;
      }
      consider(j);
    }

    if (bestJ < 0) {
      for (let j = 0; j < i; j++) consider(j);
    }

    if (bestJ >= 0) {
      const b = sorted[bestJ];
      a.used = true;
      b.used = true;
      const axis = pairToCenterline(a, b);
      if (axis.lengthMm >= opts.minWallLengthMm) {
        axes.push(axis);
        pairedCount += 1;
      }
    } else if (!a.used) {
      a.used = true;
      if (opts.requirePair) {
        unpairedSkipped += 1;
      } else if (a.length >= opts.minWallLengthMm) {
        axes.push(internalToAxis(a, [a.cadId]));
      }
    }
  }

  return { axes, pairedCount, unpairedSkipped };
}

/**
 * Drop axes that look like wall-face × dimension-line pairs.
 * Keeps the dominant thickness cluster(s); rejects sparse thick outliers.
 */
export function filterThicknessOutliers(
  axes: WallAxis[],
  options?: { maxRatio?: number; minClusterCount?: number }
): { axes: WallAxis[]; dropped: WallAxis[]; primaryThicknessMm?: number } {
  const paired = axes.filter((a) => a.thicknessMm != null && a.thicknessMm > 0);
  if (paired.length < 3) return { axes, dropped: [] };

  const clusters = clusterThicknesses(
    paired.map((a) => a.thicknessMm as number)
  );
  if (clusters.length === 0) return { axes, dropped: [] };

  const primary = clusters[0];
  const maxRatio = options?.maxRatio ?? 1.75;
  const minClusterCount = options?.minClusterCount ?? 3;
  const primaryThicknessMm = primary.thicknessMm;
  const allowedMax = Math.max(
    primaryThicknessMm * maxRatio,
    primaryThicknessMm + 80
  );

  const acceptedClusterKeys = new Set(
    clusters
      .filter(
        (c) =>
          c.count >= minClusterCount ||
          c.thicknessMm <= allowedMax ||
          Math.abs(c.thicknessMm - primaryThicknessMm) <= 40
      )
      .map((c) => Math.round(c.thicknessMm / 5) * 5)
  );

  const kept: WallAxis[] = [];
  const dropped: WallAxis[] = [];
  for (const axis of axes) {
    const t = axis.thicknessMm;
    if (t == null) {
      kept.push(axis);
      continue;
    }
    const key = Math.round(t / 5) * 5;
    const inAccepted =
      acceptedClusterKeys.has(key) ||
      t <= allowedMax ||
      clusters.some(
        (c) =>
          c.count >= minClusterCount && Math.abs(c.thicknessMm - t) <= 25
      );
    if (inAccepted) kept.push(axis);
    else dropped.push(axis);
  }

  return { axes: kept, dropped, primaryThicknessMm };
}

function mergeCollinearAxes(
  axes: WallAxis[],
  toleranceMm: number,
  mergeGapMm: number,
  minWallLengthMm: number
): WallAxis[] {
  if (axes.length <= 1) return axes;

  type AxisInternal = {
    angle: number;
    offset: number;
    tMin: number;
    tMax: number;
    cadIds: string[];
    thicknessMm?: number;
    paired?: boolean;
  };

  const internals: AxisInternal[] = axes.map((a) => {
    const x0 = a.startMm.x;
    const y0 = a.startMm.y;
    const x1 = a.endMm.x;
    const y1 = a.endMm.y;
    const length = dist(x0, y0, x1, y1);
    const angle = normalizeAngle(Math.atan2(y1 - y0, x1 - x0));
    const dx = Math.cos(angle);
    const dy = Math.sin(angle);
    const nx = -dy;
    const ny = dx;
    const mx = (x0 + x1) / 2;
    const my = (y0 + y1) / 2;
    const offset = mx * nx + my * ny;
    const t0 = x0 * dx + y0 * dy;
    const t1 = x1 * dx + y1 * dy;
    return {
      angle,
      offset,
      tMin: Math.min(t0, t1),
      tMax: Math.max(t0, t1),
      cadIds: [...a.sourceCadIds],
      thicknessMm: a.thicknessMm,
      paired: a.paired,
    };
  });

  const merged: AxisInternal[] = [];
  const used = new Array(internals.length).fill(false);

  for (let i = 0; i < internals.length; i++) {
    if (used[i]) continue;
    let cur = { ...internals[i], cadIds: [...internals[i].cadIds] };
    used[i] = true;

    let changed = true;
    while (changed) {
      changed = false;
      for (let j = 0; j < internals.length; j++) {
        if (used[j]) continue;
        const other = internals[j];
        if (!anglesMatch(cur.angle, other.angle, 2)) continue;
        if (!offsetsMatch(cur.offset, other.offset, toleranceMm)) continue;

        // Do not merge walls with clearly different thicknesses.
        if (
          cur.thicknessMm != null &&
          other.thicknessMm != null &&
          Math.abs(cur.thicknessMm - other.thicknessMm) > Math.max(25, toleranceMm)
        ) {
          continue;
        }

        const gap = Math.min(
          Math.abs(other.tMin - cur.tMax),
          Math.abs(cur.tMin - other.tMax)
        );
        const overlaps = !(
          other.tMax < cur.tMin - mergeGapMm || other.tMin > cur.tMax + mergeGapMm
        );
        if (!overlaps) continue;

        if (gap > mergeGapMm && other.tMin > cur.tMax) continue;
        if (gap > mergeGapMm && cur.tMin > other.tMax) continue;

        cur.tMin = Math.min(cur.tMin, other.tMin);
        cur.tMax = Math.max(cur.tMax, other.tMax);
        cur.cadIds.push(...other.cadIds);
        if (cur.thicknessMm == null) cur.thicknessMm = other.thicknessMm;
        else if (other.thicknessMm != null) {
          cur.thicknessMm = (cur.thicknessMm + other.thicknessMm) / 2;
        }
        cur.paired = cur.paired || other.paired;
        used[j] = true;
        changed = true;
      }
    }

    merged.push(cur);
  }

  return merged
    .map((m) =>
      axisFromParams(
        m.angle,
        m.offset,
        m.tMin,
        m.tMax,
        [...new Set(m.cadIds)],
        m.thicknessMm,
        m.paired
      )
    )
    .filter((a) => a.lengthMm >= minWallLengthMm);
}

export type ThicknessCluster = {
  thicknessMm: number;
  count: number;
};

/** Cluster measured pair gaps into thickness peaks (bin size ~10 mm). */
export function clusterThicknesses(
  thicknesses: number[],
  binMm = 5
): ThicknessCluster[] {
  if (thicknesses.length === 0) return [];
  const bins = new Map<number, { sum: number; count: number }>();
  for (const t of thicknesses) {
    if (!Number.isFinite(t) || t <= 0) continue;
    const key = Math.round(t / binMm) * binMm;
    const cur = bins.get(key) ?? { sum: 0, count: 0 };
    cur.sum += t;
    cur.count += 1;
    bins.set(key, cur);
  }
  return [...bins.entries()]
    .map(([key, v]) => ({
      thicknessMm: round1(v.sum / v.count),
      count: v.count,
      _key: key,
    }))
    .sort((a, b) => b.count - a.count || a._key - b._key)
    .map(({ thicknessMm, count }) => ({ thicknessMm, count }));
}

/** Parse thickness from wall type name, e.g. "Типовой - 200мм" → 200. */
export function parseWallTypeThicknessMm(name: string): number | null {
  if (!name) return null;
  const patterns = [
    /(\d+(?:[.,]\d+)?)\s*мм/i,
    /(\d+(?:[.,]\d+)?)\s*mm/i,
    /(?:^|[^\d])(\d{2,4})(?:\s*)$/,
  ];
  for (const re of patterns) {
    const m = name.match(re);
    if (m) {
      const v = Number(String(m[1]).replace(",", "."));
      if (Number.isFinite(v) && v >= 40 && v <= 1200) return v;
    }
  }
  return null;
}

export type WallTypeCandidate = { typeId: number; name: string; thicknessMm?: number };

/**
 * Pick closest wall type by thickness (from name or explicit thicknessMm).
 */
export function matchWallTypeByThickness(
  thicknessMm: number,
  types: WallTypeCandidate[],
  tolMm = 40
): WallTypeCandidate | null {
  let best: WallTypeCandidate | null = null;
  let bestDiff = Infinity;
  for (const t of types) {
    const tt = t.thicknessMm ?? parseWallTypeThicknessMm(t.name);
    if (tt == null) continue;
    const diff = Math.abs(tt - thicknessMm);
    if (diff < bestDiff && diff <= tolMm) {
      bestDiff = diff;
      best = { ...t, thicknessMm: tt };
    }
  }
  return best;
}

export function traceWallAxesFromCad(
  segments: CadSegment[],
  options: TraceOptions = {}
): TraceResult {
  const toleranceMm = options.toleranceMm ?? 50;
  const mergeGapMm = options.mergeGapMm ?? toleranceMm;
  const minPairGapMm = options.minPairGapMm ?? 55;
  const maxPairGapMm = options.maxPairGapMm ?? 280;
  const minWallLengthMm = options.minWallLengthMm ?? 300;
  const pairingMode = options.pairingMode ?? "centerline";
  const requirePair = options.requirePair ?? pairingMode === "centerline";
  const dropThicknessOutliers = options.dropThicknessOutliers ?? true;

  const inputSegments = segments.length;
  const afterBbox = filterSegmentsByBbox(segments, options.bboxMm);

  const internal: InternalSeg[] = [];
  for (let i = 0; i < afterBbox.length; i++) {
    const seg = toInternal(afterBbox[i], i);
    if (seg && seg.length >= minWallLengthMm * 0.5) internal.push(seg);
  }

  let axes: WallAxis[];
  let pairedCount = 0;
  let unpairedSkipped = 0;

  if (pairingMode === "raw") {
    axes = internal
      .filter((s) => s.length >= minWallLengthMm)
      .map((s) => internalToAxis(s, [s.cadId]));
  } else {
    const paired = pairDoubleLines(internal, {
      minPairGapMm,
      maxPairGapMm,
      minWallLengthMm,
      toleranceMm,
      requirePair,
    });
    axes = paired.axes;
    pairedCount = paired.pairedCount;
    unpairedSkipped = paired.unpairedSkipped;
  }

  const afterPairing = axes.length;
  axes = mergeCollinearAxes(axes, toleranceMm, mergeGapMm, minWallLengthMm);
  const afterMerge = axes.length;

  let thicknessOutliersDropped = 0;
  if (dropThicknessOutliers && pairingMode === "centerline") {
    const filtered = filterThicknessOutliers(axes, {
      maxRatio: options.maxThicknessRatio ?? 1.75,
    });
    thicknessOutliersDropped = filtered.dropped.length;
    axes = filtered.axes;
  }

  const skippedShort = afterBbox.length - afterPairing - unpairedSkipped;
  const thicknessClusters = clusterThicknesses(
    axes.map((a) => a.thicknessMm).filter((t): t is number => t != null && t > 0)
  );

  return {
    axes,
    stats: {
      inputSegments,
      afterBbox: afterBbox.length,
      afterPairing,
      afterMerge,
      pairedCount,
      unpairedSkipped,
      skippedShort,
      thicknessOutliersDropped,
    },
    thicknessClusters,
  };
}

/** Perpendicular distance from point (px,py) to segment (x0,y0)-(x1,y1). */
function pointToSegmentDist(
  px: number,
  py: number,
  x0: number,
  y0: number,
  x1: number,
  y1: number
): number {
  const dx = x1 - x0;
  const dy = y1 - y0;
  const lenSq = dx * dx + dy * dy;
  if (lenSq < 1e-6) return dist(px, py, x0, y0);
  let t = ((px - x0) * dx + (py - y0) * dy) / lenSq;
  t = Math.max(0, Math.min(1, t));
  const cx = x0 + t * dx;
  const cy = y0 + t * dy;
  return dist(px, py, cx, cy);
}

export type VerifyResult = {
  maxDeviationMm: number;
  meanDeviationMm: number;
  failedAxes: Array<{ index: number; deviationMm: number }>;
};

/**
 * Verify centerlines against CAD faces.
 * For paired walls, expected distance to nearest face ≈ thickness/2 —
 * do not fail thick walls just because default tolerance is 50 mm.
 */
export function verifyAxesAgainstCad(
  axes: WallAxis[],
  cadSegments: CadSegment[],
  toleranceMm: number
): VerifyResult {
  const failedAxes: Array<{ index: number; deviationMm: number }> = [];
  let maxDev = 0;
  let sumDev = 0;

  for (let i = 0; i < axes.length; i++) {
    const axis = axes[i];
    const mx = (axis.startMm.x + axis.endMm.x) / 2;
    const my = (axis.startMm.y + axis.endMm.y) / 2;

    let minDist = Infinity;
    for (const seg of cadSegments) {
      const d = pointToSegmentDist(
        mx,
        my,
        seg.startMm.x,
        seg.startMm.y,
        seg.endMm.x,
        seg.endMm.y
      );
      if (d < minDist) minDist = d;
    }

    if (!Number.isFinite(minDist)) minDist = 0;
    maxDev = Math.max(maxDev, minDist);
    sumDev += minDist;

    const expectedHalf =
      axis.thicknessMm != null && axis.thicknessMm > 0
        ? axis.thicknessMm / 2
        : 0;
    const allowed = Math.max(toleranceMm, expectedHalf + toleranceMm * 0.5);
    // Distance from centerline to face should be near half-thickness.
    const residual = Math.abs(minDist - expectedHalf);
    const failBy =
      expectedHalf > 0 ? residual > toleranceMm && minDist > allowed : minDist > toleranceMm;

    if (failBy) {
      failedAxes.push({ index: i, deviationMm: round1(minDist) });
    }
  }

  return {
    maxDeviationMm: round1(maxDev),
    meanDeviationMm: axes.length > 0 ? round1(sumDev / axes.length) : 0,
    failedAxes,
  };
}
