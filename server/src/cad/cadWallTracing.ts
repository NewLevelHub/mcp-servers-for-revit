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
};

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
};

export type TraceOptions = {
  toleranceMm?: number;
  mergeGapMm?: number;
  minPairGapMm?: number;
  maxPairGapMm?: number;
  minWallLengthMm?: number;
  bboxMm?: BboxMm;
  pairingMode?: "centerline" | "raw";
};

export type TraceStats = {
  inputSegments: number;
  afterBbox: number;
  afterPairing: number;
  afterMerge: number;
  pairedCount: number;
  skippedShort: number;
};

export type TraceResult = {
  axes: WallAxis[];
  stats: TraceStats;
};

type InternalSeg = {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  cadId: string;
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
  // Cohen–Sutherland simplified: check if segment crosses bbox
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

  const dx = (x1 - x0) / length;
  const dy = (y1 - y0) / length;
  const angle = normalizeAngle(Math.atan2(dy, dx));
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
  };
}

function axisFromParams(
  angle: number,
  offset: number,
  tMin: number,
  tMax: number,
  cadIds: string[]
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
  };
}

function overlapT(a: InternalSeg, b: InternalSeg): number {
  const start = Math.max(a.tMin, b.tMin);
  const end = Math.min(a.tMax, b.tMax);
  return Math.max(0, end - start);
}

function pairToCenterline(a: InternalSeg, b: InternalSeg): WallAxis {
  const angle = a.angle;
  const offset = (a.offset + b.offset) / 2;
  const tMin = Math.max(a.tMin, b.tMin);
  const tMax = Math.min(a.tMax, b.tMax);
  return axisFromParams(angle, offset, tMin, tMax, [a.cadId, b.cadId]);
}

function pairDoubleLines(
  segments: InternalSeg[],
  opts: Required<
    Pick<TraceOptions, "minPairGapMm" | "maxPairGapMm" | "minWallLengthMm" | "toleranceMm">
  >
): { axes: WallAxis[]; pairedCount: number } {
  const axes: WallAxis[] = [];
  let pairedCount = 0;
  const angleTolDeg = 2;

  const byAngle = new Map<number, InternalSeg[]>();
  for (const seg of segments) {
    const bin = Math.round(seg.angle / DEG);
    let list = byAngle.get(bin);
    if (!list) {
      list = [];
      byAngle.set(bin, list);
    }
    list.push(seg);
  }

  for (const group of byAngle.values()) {
    group.sort((a, b) => a.offset - b.offset);

    for (let i = 0; i < group.length; i++) {
      const a = group[i];
      if (a.used) continue;

      let bestJ = -1;
      let bestOverlap = 0;

      for (let j = i + 1; j < group.length; j++) {
        const b = group[j];
        if (b.used) continue;
        if (!anglesMatch(a.angle, b.angle, angleTolDeg)) continue;

        const gap = Math.abs(b.offset - a.offset);
        if (gap < opts.minPairGapMm || gap > opts.maxPairGapMm) continue;

        const ov = overlapT(a, b);
        if (ov < Math.min(a.length, b.length) * 0.25 && ov < 200) continue;

        if (ov > bestOverlap) {
          bestOverlap = ov;
          bestJ = j;
        }
      }

      if (bestJ >= 0) {
        const b = group[bestJ];
        a.used = true;
        b.used = true;
        const axis = pairToCenterline(a, b);
        if (axis.lengthMm >= opts.minWallLengthMm) {
          axes.push(axis);
          pairedCount += 1;
        }
      } else if (!a.used) {
        a.used = true;
        if (a.length >= opts.minWallLengthMm) {
          axes.push(internalToAxis(a, [a.cadId]));
        }
      }
    }
  }

  return { axes, pairedCount };
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
  };

  const internals: AxisInternal[] = axes.map((a) => {
    const x0 = a.startMm.x;
    const y0 = a.startMm.y;
    const x1 = a.endMm.x;
    const y1 = a.endMm.y;
    const length = dist(x0, y0, x1, y1);
    const dx = length > 0 ? (x1 - x0) / length : 1;
    const dy = length > 0 ? (y1 - y0) / length : 0;
    const angle = normalizeAngle(Math.atan2(dy, dx));
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

        const gap = Math.min(
          Math.abs(other.tMin - cur.tMax),
          Math.abs(cur.tMin - other.tMax)
        );
        const overlaps = !(other.tMax < cur.tMin - mergeGapMm || other.tMin > cur.tMax + mergeGapMm);
        if (!overlaps) continue;

        if (gap > mergeGapMm && other.tMin > cur.tMax) continue;
        if (gap > mergeGapMm && cur.tMin > other.tMax) continue;

        cur.tMin = Math.min(cur.tMin, other.tMin);
        cur.tMax = Math.max(cur.tMax, other.tMax);
        cur.cadIds.push(...other.cadIds);
        used[j] = true;
        changed = true;
      }
    }

    merged.push(cur);
  }

  return merged
    .map((m) => axisFromParams(m.angle, m.offset, m.tMin, m.tMax, [...new Set(m.cadIds)]))
    .filter((a) => a.lengthMm >= minWallLengthMm);
}

export function traceWallAxesFromCad(
  segments: CadSegment[],
  options: TraceOptions = {}
): TraceResult {
  const toleranceMm = options.toleranceMm ?? 50;
  const mergeGapMm = options.mergeGapMm ?? toleranceMm;
  const minPairGapMm = options.minPairGapMm ?? 50;
  const maxPairGapMm = options.maxPairGapMm ?? 500;
  const minWallLengthMm = options.minWallLengthMm ?? 300;
  const pairingMode = options.pairingMode ?? "centerline";

  const inputSegments = segments.length;
  const afterBbox = filterSegmentsByBbox(segments, options.bboxMm);

  const internal: InternalSeg[] = [];
  for (let i = 0; i < afterBbox.length; i++) {
    const seg = toInternal(afterBbox[i], i);
    if (seg && seg.length >= minWallLengthMm * 0.5) internal.push(seg);
  }

  let axes: WallAxis[];
  let pairedCount = 0;

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
    });
    axes = paired.axes;
    pairedCount = paired.pairedCount;
  }

  const afterPairing = axes.length;
  axes = mergeCollinearAxes(axes, toleranceMm, mergeGapMm, minWallLengthMm);
  const afterMerge = axes.length;

  const skippedShort = afterBbox.length - afterPairing;

  return {
    axes,
    stats: {
      inputSegments,
      afterBbox: afterBbox.length,
      afterPairing,
      afterMerge,
      pairedCount,
      skippedShort,
    },
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
    if (minDist > toleranceMm) {
      failedAxes.push({ index: i, deviationMm: round1(minDist) });
    }
  }

  return {
    maxDeviationMm: round1(maxDev),
    meanDeviationMm: axes.length > 0 ? round1(sumDev / axes.length) : 0,
    failedAxes,
  };
}
