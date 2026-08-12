/**
 * REV-156: band tracing — walls drawn as a stack of parallel lines.
 *
 * The centerline pairing in cadWallTracing pairs each face line with its NEAREST parallel
 * neighbour. That is right for a DWG that draws a wall as two lines, and wrong for one that
 * draws its build-up: on «двг2.dwg» a 125 mm partition arrives as six lines
 * (0 / 12.5 / 25 — core 75 — 100 / 112.5 / 125), so the nearest neighbour of every line is a
 * finish layer 12.5 mm away. The tracer reported thicknesses of 62.5, 81.3, 93.8 and 112.5 mm,
 * split one wall into two overlapping axes, and 476 of ~900 wall lines had a neighbour at
 * 12.5 mm. Raising minPairGapMm does not fix it — it just picks a different wrong pair.
 *
 * Here a wall is a BAND: two face clusters with an empty core between them.
 *   1. group lines into face clusters — close in position AND running alongside each other
 *   2. pair two clusters across a core no wider than maxCoreMm
 *   3. the wall runs where both OUTER faces are actually drawn
 *   4. reject the pair where a third cluster runs between them (that is two walls and a room)
 *
 * On «двг2.dwg» this turned 210 axes with 14 thickness clusters into 156 walls whose
 * thicknesses are 75/100/125/150/200/250/300 mm and nothing else.
 */

import type { CadSegment, PointMm, WallAxis } from "./cadWallTracing.js";

export type BandTracingOptions = {
  /** Thinnest band accepted as a wall (mm). */
  minThicknessMm?: number;
  /** Thickest band accepted as a wall (mm). */
  maxThicknessMm?: number;
  /** Step between lines inside one face (finish layers) — above this a gap is a core. */
  layerGapMm?: number;
  /** Widest empty core between the two faces (mm). */
  maxCoreMm?: number;
  /** Two lines join a cluster only if they run alongside for at least this much (mm). */
  minOverlapMm?: number;
  /** Drop walls shorter than this (mm). */
  minWallLengthMm?: number;
  /** Join collinear runs across a door/window break up to this wide (mm). */
  bridgeGapMm?: number;
  /** Snap a measured thickness onto one of these when within snapToleranceMm. */
  canonicalThicknessesMm?: number[];
  snapToleranceMm?: number;
};

export type BandTracingResult = {
  axes: WallAxis[];
  stats: {
    inputSegments: number;
    horizontal: number;
    vertical: number;
    skewed: number;
    faceClusters: number;
    /** Candidates dropped because another face ran through the core. */
    coreBlocked: number;
    /** Candidates dropped as nested inside a thicker wall. */
    nested: number;
  };
  thicknessClusters: { thicknessMm: number; count: number; totalLengthMm: number }[];
};

type Interval = [number, number];
type Line = { pos: number; a: number; b: number; cadId?: string };

const DEFAULTS: Required<
  Omit<BandTracingOptions, "canonicalThicknessesMm">
> & { canonicalThicknessesMm: number[] } = {
  minThicknessMm: 60,
  maxThicknessMm: 420,
  layerGapMm: 30,
  maxCoreMm: 320,
  minOverlapMm: 250,
  minWallLengthMm: 400,
  bridgeGapMm: 2600,
  canonicalThicknessesMm: [75, 100, 125, 150, 200, 250, 300, 380, 400],
  snapToleranceMm: 20,
};

export function mergeIntervals(list: Interval[], bridge = 0): Interval[] {
  if (list.length === 0) return [];
  const sorted = [...list].sort((x, y) => x[0] - y[0]);
  const out: Interval[] = [[sorted[0][0], sorted[0][1]]];
  for (let i = 1; i < sorted.length; i++) {
    const cur = out[out.length - 1];
    if (sorted[i][0] <= cur[1] + bridge) cur[1] = Math.max(cur[1], sorted[i][1]);
    else out.push([sorted[i][0], sorted[i][1]]);
  }
  return out;
}

export function intersectIntervals(A: Interval[], B: Interval[]): Interval[] {
  const out: Interval[] = [];
  let i = 0;
  let j = 0;
  while (i < A.length && j < B.length) {
    const lo = Math.max(A[i][0], B[j][0]);
    const hi = Math.min(A[i][1], B[j][1]);
    if (hi > lo) out.push([lo, hi]);
    if (A[i][1] < B[j][1]) i++;
    else j++;
  }
  return out;
}

function totalLength(list: Interval[]): number {
  return list.reduce((sum, [a, b]) => sum + (b - a), 0);
}

function overlap(a: Interval, b: Interval): number {
  return Math.min(a[1], b[1]) - Math.max(a[0], b[0]);
}

type FaceCluster = {
  posMin: number;
  posMax: number;
  /** Coverage of the outermost line on the low side. */
  lo: Interval[];
  /** Coverage of the outermost line on the high side. */
  hi: Interval[];
  /** Everything the cluster covers — used to test whether it blocks a core. */
  all: Interval[];
};

/**
 * Group lines into face clusters. Two lines belong together when they are within
 * `layerGapMm` AND overlap along the wall — without the overlap test, lines from opposite
 * ends of the plan that happen to share a coordinate collapse into one cluster.
 */
function buildFaceClusters(lines: Line[], opt: typeof DEFAULTS): FaceCluster[] {
  const parent = lines.map((_, i) => i);
  const find = (x: number): number =>
    parent[x] === x ? x : (parent[x] = find(parent[x]));
  const union = (x: number, y: number) => {
    const a = find(x);
    const b = find(y);
    if (a !== b) parent[a] = b;
  };

  // Sort by position so the inner loop can stop early.
  const order = lines.map((_, i) => i).sort((i, j) => lines[i].pos - lines[j].pos);
  for (let ii = 0; ii < order.length; ii++) {
    const i = order[ii];
    for (let jj = ii + 1; jj < order.length; jj++) {
      const j = order[jj];
      if (lines[j].pos - lines[i].pos > opt.layerGapMm) break;
      if (overlap([lines[i].a, lines[i].b], [lines[j].a, lines[j].b]) >= opt.minOverlapMm) {
        union(i, j);
      }
    }
  }

  const groups = new Map<number, number[]>();
  lines.forEach((_, i) => {
    const root = find(i);
    const list = groups.get(root);
    if (list) list.push(i);
    else groups.set(root, [i]);
  });

  const clusters: FaceCluster[] = [];
  for (const members of groups.values()) {
    let posMin = Infinity;
    let posMax = -Infinity;
    for (const i of members) {
      posMin = Math.min(posMin, lines[i].pos);
      posMax = Math.max(posMax, lines[i].pos);
    }
    const lo: Interval[] = [];
    const hi: Interval[] = [];
    const all: Interval[] = [];
    for (const i of members) {
      const iv: Interval = [lines[i].a, lines[i].b];
      all.push(iv);
      if (lines[i].pos <= posMin + 0.3) lo.push(iv);
      if (lines[i].pos >= posMax - 0.3) hi.push(iv);
    }
    clusters.push({
      posMin,
      posMax,
      lo: mergeIntervals(lo),
      hi: mergeIntervals(hi),
      all: mergeIntervals(all),
    });
  }
  return clusters.sort((a, b) => a.posMin - b.posMin);
}

type Band = { pos: number; thicknessMm: number; a: number; b: number };

function bandsFromClusters(
  clusters: FaceCluster[],
  opt: typeof DEFAULTS,
  counters: { coreBlocked: number }
): Band[] {
  const bands: Band[] = [];

  // A partition whose every step is under layerGapMm arrives as a single cluster; its own
  // two outer faces are the wall.
  for (const c of clusters) {
    const t = c.posMax - c.posMin;
    if (t < opt.minThicknessMm || t > opt.maxThicknessMm) continue;
    const both = intersectIntervals(c.lo, c.hi);
    if (totalLength(both) < opt.minOverlapMm) continue;
    for (const [a, b] of mergeIntervals(both, opt.bridgeGapMm)) {
      if (b - a >= opt.minWallLengthMm) {
        bands.push({ pos: (c.posMin + c.posMax) / 2, thicknessMm: t, a, b });
      }
    }
  }

  for (let i = 0; i < clusters.length; i++) {
    for (let j = i + 1; j < clusters.length; j++) {
      const c0 = clusters[i];
      const c1 = clusters[j];
      const t = c1.posMax - c0.posMin;
      if (t < opt.minThicknessMm) continue;
      if (t > opt.maxThicknessMm) break;
      const core = c1.posMin - c0.posMax;
      if (core < 0 || core > opt.maxCoreMm) continue;

      let both = intersectIntervals(c0.lo, c1.hi);
      if (totalLength(both) < opt.minOverlapMm) continue;

      // A wall's core is empty. Where a third face runs between these two, the span is two
      // walls with a room between them — cut that stretch out rather than bridging it.
      const before = totalLength(both);
      for (const m of clusters) {
        if (m === c0 || m === c1) continue;
        if (m.posMin <= c0.posMax + 0.3 || m.posMax >= c1.posMin - 0.3) continue;
        both = subtractIntervals(both, m.all);
        if (both.length === 0) break;
      }
      if (totalLength(both) < before) counters.coreBlocked++;
      if (totalLength(both) < opt.minOverlapMm) continue;

      for (const [a, b] of mergeIntervals(both, opt.bridgeGapMm)) {
        if (b - a >= opt.minWallLengthMm) {
          bands.push({ pos: (c0.posMin + c1.posMax) / 2, thicknessMm: t, a, b });
        }
      }
    }
  }
  return bands;
}

export function subtractIntervals(A: Interval[], B: Interval[]): Interval[] {
  let out: Interval[] = A.map((x) => [x[0], x[1]]);
  for (const [b0, b1] of B) {
    const next: Interval[] = [];
    for (const [a0, a1] of out) {
      if (b1 <= a0 || b0 >= a1) {
        next.push([a0, a1]);
        continue;
      }
      if (b0 > a0) next.push([a0, b0]);
      if (b1 < a1) next.push([b1, a1]);
    }
    out = next;
  }
  return out;
}

/** A band grown from an interior finish layer sits inside the real wall — drop it. */
function dropNested(
  bands: (Band & { dir: "h" | "v" })[],
  counters: { nested: number }
): (Band & { dir: "h" | "v" })[] {
  const slab = (w: Band): Interval => [
    w.pos - w.thicknessMm / 2,
    w.pos + w.thicknessMm / 2,
  ];
  const kept = bands.filter((w) => {
    const [w0, w1] = slab(w);
    return !bands.some((k) => {
      if (k === w || k.dir !== w.dir) return false;
      const [k0, k1] = slab(k);
      if (!(k0 <= w0 + 1 && k1 >= w1 - 1)) return false;
      // Two identical bands: keep the longer one, drop the other exactly once.
      if (k.thicknessMm === w.thicknessMm && k.b - k.a <= w.b - w.a) return false;
      return Math.min(w.b, k.b) - Math.max(w.a, k.a) > 0.7 * (w.b - w.a);
    });
  });
  counters.nested = bands.length - kept.length;
  return kept;
}

function snapThickness(t: number, opt: typeof DEFAULTS): number {
  let best: number | null = null;
  for (const c of opt.canonicalThicknessesMm) {
    const d = Math.abs(c - t);
    if (d <= opt.snapToleranceMm && (best === null || d < Math.abs(best - t))) best = c;
  }
  return best ?? Math.round(t * 10) / 10;
}

/**
 * Trace wall axes from CAD segments by band detection.
 * Only axis-aligned segments take part — skewed lines are counted and left alone.
 */
export function traceWallBandsFromCad(
  segments: CadSegment[],
  options?: BandTracingOptions
): BandTracingResult {
  const opt = { ...DEFAULTS, ...options };
  const horizontal: Line[] = [];
  const vertical: Line[] = [];
  let skewed = 0;

  for (const s of segments) {
    const dx = s.endMm.x - s.startMm.x;
    const dy = s.endMm.y - s.startMm.y;
    if (Math.abs(dy) < 0.6 && Math.abs(dx) > 0.6) {
      horizontal.push({
        pos: s.startMm.y,
        a: Math.min(s.startMm.x, s.endMm.x),
        b: Math.max(s.startMm.x, s.endMm.x),
        cadId: s.cadId,
      });
    } else if (Math.abs(dx) < 0.6 && Math.abs(dy) > 0.6) {
      vertical.push({
        pos: s.startMm.x,
        a: Math.min(s.startMm.y, s.endMm.y),
        b: Math.max(s.startMm.y, s.endMm.y),
        cadId: s.cadId,
      });
    } else if (Math.abs(dx) > 0.6 || Math.abs(dy) > 0.6) {
      skewed++;
    }
  }

  const counters = { coreBlocked: 0, nested: 0 };
  const hClusters = buildFaceClusters(horizontal, opt);
  const vClusters = buildFaceClusters(vertical, opt);
  const bands = [
    ...bandsFromClusters(hClusters, opt, counters).map((b) => ({
      ...b,
      dir: "h" as const,
    })),
    ...bandsFromClusters(vClusters, opt, counters).map((b) => ({
      ...b,
      dir: "v" as const,
    })),
  ];

  const kept = dropNested(bands, counters).map((b) => ({
    ...b,
    thicknessMm: snapThickness(b.thicknessMm, opt),
  }));

  // A door breaks the drawn faces and the run may resume from a different cluster pair, so
  // the two halves stay separate walls and an opening lands past the end of both. Join
  // collinear runs of equal thickness across opening-sized gaps.
  const byRun = new Map<string, (typeof kept)[number][]>();
  for (const w of kept) {
    const key = `${w.dir}|${Math.round(w.pos * 2) / 2}|${w.thicknessMm}`;
    const list = byRun.get(key);
    if (list) list.push(w);
    else byRun.set(key, [w]);
  }

  const axes: WallAxis[] = [];
  const clusterTotals = new Map<number, { count: number; totalLengthMm: number }>();
  for (const [key, list] of byRun) {
    const [dir, posText, thickText] = key.split("|");
    const pos = Number(posText);
    const thicknessMm = Number(thickText);
    for (const [a, b] of mergeIntervals(
      list.map((w) => [w.a, w.b] as Interval),
      opt.bridgeGapMm
    )) {
      if (b - a < opt.minWallLengthMm) continue;
      const startMm: PointMm =
        dir === "h" ? { x: a, y: pos, z: 0 } : { x: pos, y: a, z: 0 };
      const endMm: PointMm =
        dir === "h" ? { x: b, y: pos, z: 0 } : { x: pos, y: b, z: 0 };
      axes.push({
        startMm,
        endMm,
        lengthMm: b - a,
        sourceCadIds: [],
        thicknessMm,
        paired: true,
      });
      const stat = clusterTotals.get(thicknessMm) ?? { count: 0, totalLengthMm: 0 };
      stat.count++;
      stat.totalLengthMm += b - a;
      clusterTotals.set(thicknessMm, stat);
    }
  }

  return {
    axes,
    stats: {
      inputSegments: segments.length,
      horizontal: horizontal.length,
      vertical: vertical.length,
      skewed,
      faceClusters: hClusters.length + vClusters.length,
      coreBlocked: counters.coreBlocked,
      nested: counters.nested,
    },
    thicknessClusters: [...clusterTotals.entries()]
      .map(([thicknessMm, s]) => ({ thicknessMm, ...s }))
      .sort((a, b) => b.totalLengthMm - a.totalLengthMm),
  };
}
