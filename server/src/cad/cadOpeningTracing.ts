/**
 * REV-147 / REV-148: CAD opening tracing — cluster door/window symbols, match host walls.
 * Pure geometry (mm); unit-tested without Revit.
 *
 * REV-148: prefer MCUT leaf over OTLN swing; reject far end-snaps; bridge/stub hosts
 * when CAD door sits in a gap between short wall segments (WC stalls).
 */

import type { CadSegment, CadBlock, PointMm, BboxMm } from "./cadWallTracing.js";
import { computeSegmentsBbox } from "./cadWallTracing.js";

export type OpeningKind = "door" | "window";

export const DEFAULT_DOOR_LAYER_PATTERNS = ["a-door", "door", "двер"];
/** Prefer explicit window layers; broad a-glaz kept but curtain sublayers excluded below. */
export const DEFAULT_WINDOW_LAYER_PATTERNS = [
  "a-wind",
  "window",
  "окн",
  "остек",
  "a-glaz",
];
/** Curtain-wall glazing / mullions are walls (REV-140), not window instances. */
export const DEFAULT_EXCLUDE_OPENING_LAYERS = [
  "a-glaz-curt",
  "glaz-curt",
  "curtain",
  "a-glaz-cwmg",
  "glaz-cwmg",
  "cwmg",
];

export type HostWall = {
  id: number;
  startMm: PointMm;
  endMm: PointMm;
  lengthMm?: number;
  /** Optional wall type id (for bridge create). */
  typeId?: number;
  typeName?: string;
  /** REV-149: donor dimensions so a bridge wall matches its neighbours. */
  heightMm?: number;
  widthMm?: number;
  baseOffsetMm?: number;
};

/**
 * REV-149: a door swing arc read straight from the DWG.
 * The arc centre is the hinge and sits on the wall line; one endpoint is the closed leaf
 * tip (also on the wall), the other is the open leaf tip. Which is which only becomes
 * decidable once the host wall direction is known — see resolveArcSwingOnWall.
 */
export type ArcSwing = {
  arcId: string;
  hingeMm: PointMm;
  radiusMm: number;
  endAMm: PointMm;
  endBMm: PointMm;
  layer?: string;
  blockIndex?: number;
  blockName?: string;
  blockMirrored?: boolean;
};

/** Geometry of an arc-derived opening once the host wall is known. */
export type ResolvedArcSwing = {
  centerMm: PointMm;
  widthMm: number;
  leafDir: { x: number; y: number };
  swingNormal: { x: number; y: number };
  hingeMm: PointMm;
  /** Along-wall unit direction from the opening centre toward the hinge. */
  hingeDir: { x: number; y: number };
};

export type DetectedOpening = {
  kind: OpeningKind;
  centerMm: PointMm;
  widthMm: number;
  heightMm?: number;
  layer?: string;
  sourceCadIds: string[];
  bboxMm: BboxMm;
  segmentCount: number;
  /** Unit direction along the door leaf (parallel to host wall). */
  leafDir?: { x: number; y: number };
  /**
   * Unit normal pointing toward the CAD swing / open side of the leaf.
   * Used to offset locationPoint / facing so Revit matches the underlay.
   */
  swingNormal?: { x: number; y: number };
  /** REV-149: exact swing geometry when the opening came from a DWG arc. */
  arc?: ArcSwing;
  /** REV-149: hinge point, once resolved against a host wall. */
  hingeMm?: PointMm;
  /** REV-149: how the opening was detected — arc is exact, the rest are heuristics. */
  detection?: "arc" | "leaf" | "cluster";
};

/** Synthetic host wall to create across a CAD opening gap before placing the door. */
export type OpeningBridge = {
  startMm: PointMm;
  endMm: PointMm;
  /** Donor wall for type/thickness (nearest prostenok). */
  donorWallId: number;
  leftWallId?: number;
  rightWallId?: number;
  kind: "gap" | "stub";
};

export type PlannedOpening = DetectedOpening & {
  /** Existing host wall id, or 0 when bridge must be created first. */
  hostWallId: number;
  locationMm: PointMm;
  hostDistanceMm: number;
  paramT: number;
  typeId?: number;
  matchedTypeName?: string;
  /** Along-wall distance from CAD center to clamped point on existing segment. */
  alongOvershootMm?: number;
  /** When set, create this wall segment before create_point_based_element. */
  bridge?: OpeningBridge;
  /**
   * REV-149: along-wall unit direction from the opening centre toward the hinge.
   * Sent to Revit as a hand hint so the door is not mirrored against the DWG.
   */
  hingeDir?: { x: number; y: number };
};

export type OpeningTypeCandidate = {
  typeId: number;
  name: string;
  widthMm?: number;
};

export type TraceOpeningsOptions = {
  kind: OpeningKind;
  layerPatterns?: string[];
  excludeLayers?: string[];
  clusterGapMm?: number;
  minOpeningWidthMm?: number;
  maxOpeningWidthMm?: number;
  minSegmentsPerCluster?: number;
  /** Merge openings whose centers are closer than this (default 400). */
  dedupeDistanceMm?: number;
  bboxMm?: BboxMm;
  /** REV-149: block instances from get_cad_link_geometry (for names / mirror flags). */
  blocks?: CadBlock[];
  /**
   * REV-149: when false, skip the arc path and fall back to the REV-147/148 heuristics.
   * Only useful for DWGs whose swings were exploded into plain polylines.
   */
  useArcs?: boolean;
};

export type MatchHostsOptions = {
  maxHostDistanceMm?: number;
  /** Min clear distance from wall end / junction hint (informational). Default 100. */
  minEndClearanceMm?: number;
  preferredEndClearanceMm?: number;
  /**
   * Max along-wall overshoot still accepted as on-segment host (default 80).
   * Larger overshoots go to gap/stub bridge matching.
   */
  maxAlongOvershootMm?: number;
  /** Max gap between colinear wall ends to bridge (default max(width*2.5, 1500)). */
  maxGapMm?: number;
  /** Allow creating stub/gap bridge hosts (default true). */
  allowBridge?: boolean;
};

function dist(ax: number, ay: number, bx: number, by: number): number {
  return Math.hypot(bx - ax, by - ay);
}

function segmentLength(seg: CadSegment): number {
  if (seg.lengthMm != null && seg.lengthMm > 0) return seg.lengthMm;
  return dist(seg.startMm.x, seg.startMm.y, seg.endMm.x, seg.endMm.y);
}

function wallLength(w: HostWall): number {
  if (w.lengthMm != null && w.lengthMm > 0) return w.lengthMm;
  return dist(w.startMm.x, w.startMm.y, w.endMm.x, w.endMm.y);
}

function layerMatches(layer: string, patterns: string[]): boolean {
  const lower = (layer ?? "").toLowerCase();
  if (!lower) return false;
  return patterns.some((p) => lower.includes(p.toLowerCase()));
}

function layerExcluded(layer: string, exclude: string[]): boolean {
  const lower = (layer ?? "").toLowerCase();
  return exclude.some(
    (p) => lower.includes(p.toLowerCase()) || lower === p.toLowerCase()
  );
}

function segmentInBbox(seg: CadSegment, bbox: BboxMm): boolean {
  const xs = [seg.startMm.x, seg.endMm.x];
  const ys = [seg.startMm.y, seg.endMm.y];
  const minX = Math.min(...xs);
  const maxX = Math.max(...xs);
  const minY = Math.min(...ys);
  const maxY = Math.max(...ys);
  return !(maxX < bbox.minX || minX > bbox.maxX || maxY < bbox.minY || minY > bbox.maxY);
}

/**
 * Keep CAD segments that look like door/window symbols on matching layers.
 * Unlike wall tracing, diagonals / arcs (door swings) are kept.
 */
export function filterSegmentsForOpenings(
  segments: CadSegment[],
  options: {
    layerPatterns: string[];
    excludeLayers?: string[];
    bboxMm?: BboxMm;
    minLengthMm?: number;
  }
): { segments: CadSegment[]; stats: { input: number; excluded: number } } {
  const exclude = options.excludeLayers ?? DEFAULT_EXCLUDE_OPENING_LAYERS;
  const minLen = options.minLengthMm ?? 0;
  let excluded = 0;
  const kept: CadSegment[] = [];

  for (const seg of segments) {
    const layer = seg.layer ?? "";
    if (layerExcluded(layer, exclude)) {
      excluded++;
      continue;
    }
    if (!layerMatches(layer, options.layerPatterns)) {
      excluded++;
      continue;
    }
    if (minLen > 0 && segmentLength(seg) < minLen) {
      excluded++;
      continue;
    }
    if (options.bboxMm && !segmentInBbox(seg, options.bboxMm)) {
      excluded++;
      continue;
    }
    kept.push(seg);
  }

  return { segments: kept, stats: { input: segments.length, excluded } };
}

function bboxOfSegments(segs: CadSegment[]): BboxMm {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const seg of segs) {
    for (const p of [seg.startMm, seg.endMm]) {
      if (p.x < minX) minX = p.x;
      if (p.y < minY) minY = p.y;
      if (p.x > maxX) maxX = p.x;
      if (p.y > maxY) maxY = p.y;
    }
  }
  return { minX, minY, maxX, maxY };
}

function pointsClose(
  ax: number,
  ay: number,
  bx: number,
  by: number,
  gapMm: number
): boolean {
  return dist(ax, ay, bx, by) <= gapMm;
}

function segmentsConnected(a: CadSegment, b: CadSegment, gapMm: number): boolean {
  const pairs: Array<[PointMm, PointMm]> = [
    [a.startMm, b.startMm],
    [a.startMm, b.endMm],
    [a.endMm, b.startMm],
    [a.endMm, b.endMm],
  ];
  for (const [p, q] of pairs) {
    if (pointsClose(p.x, p.y, q.x, q.y, gapMm)) return true;
  }
  // Midpoint proximity for overlapping door jambs / arcs
  const amx = (a.startMm.x + a.endMm.x) / 2;
  const amy = (a.startMm.y + a.endMm.y) / 2;
  const bmx = (b.startMm.x + b.endMm.x) / 2;
  const bmy = (b.startMm.y + b.endMm.y) / 2;
  if (pointsClose(amx, amy, bmx, bmy, gapMm)) return true;

  const ab = bboxOfSegments([a]);
  const bb = bboxOfSegments([b]);
  const pad = gapMm;
  return !(
    ab.maxX + pad < bb.minX ||
    bb.maxX + pad < ab.minX ||
    ab.maxY + pad < bb.minY ||
    bb.maxY + pad < ab.minY
  );
}

/**
 * Union nearby symbol segments into opening clusters.
 */
export function clusterOpeningSegments(
  segments: CadSegment[],
  clusterGapMm = 450
): CadSegment[][] {
  const n = segments.length;
  if (n === 0) return [];
  const parent = Array.from({ length: n }, (_, i) => i);
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

  for (let i = 0; i < n; i++) {
    for (let j = i + 1; j < n; j++) {
      if (segmentsConnected(segments[i], segments[j], clusterGapMm)) {
        unite(i, j);
      }
    }
  }

  const groups = new Map<number, CadSegment[]>();
  for (let i = 0; i < n; i++) {
    const r = find(i);
    const list = groups.get(r) ?? [];
    list.push(segments[i]);
    groups.set(r, list);
  }
  return [...groups.values()];
}

/**
 * Estimate opening width from symbol bbox.
 * Door swing bbox is often ~W×W; take the side closest to typical opening
 * (prefer side in [min,max], else longer side clamped).
 */
export function estimateOpeningWidthMm(
  bbox: BboxMm,
  minWidthMm: number,
  maxWidthMm: number
): number {
  const w = Math.abs(bbox.maxX - bbox.minX);
  const h = Math.abs(bbox.maxY - bbox.minY);
  const longer = Math.max(w, h);
  const shorter = Math.min(w, h);

  const candidates = [longer, shorter].filter(
    (v) => v >= minWidthMm * 0.7 && v <= maxWidthMm * 1.25
  );
  if (candidates.length === 0) {
    return Math.max(minWidthMm, Math.min(maxWidthMm, longer || shorter || minWidthMm));
  }
  // Prefer values near common door widths (700–1200)
  candidates.sort((a, b) => {
    const score = (v: number) => Math.abs(v - 900);
    return score(a) - score(b);
  });
  return Math.round(Math.max(minWidthMm, Math.min(maxWidthMm, candidates[0])));
}

function refineWidthAlongWall(
  bbox: BboxMm,
  wall: HostWall,
  minWidthMm: number,
  maxWidthMm: number
): number {
  const dx = wall.endMm.x - wall.startMm.x;
  const dy = wall.endMm.y - wall.startMm.y;
  const len = Math.hypot(dx, dy);
  if (len < 1) return estimateOpeningWidthMm(bbox, minWidthMm, maxWidthMm);

  const ux = dx / len;
  const uy = dy / len;
  const corners: PointMm[] = [
    { x: bbox.minX, y: bbox.minY },
    { x: bbox.maxX, y: bbox.minY },
    { x: bbox.minX, y: bbox.maxY },
    { x: bbox.maxX, y: bbox.maxY },
  ];
  const projs = corners.map((c) => c.x * ux + c.y * uy);
  const extent = Math.max(...projs) - Math.min(...projs);
  if (extent >= minWidthMm * 0.7 && extent <= maxWidthMm * 1.25) {
    return Math.round(Math.max(minWidthMm, Math.min(maxWidthMm, extent)));
  }
  return estimateOpeningWidthMm(bbox, minWidthMm, maxWidthMm);
}

function isOrthoSeg(seg: CadSegment, tol = 0.15): boolean {
  const dx = Math.abs(seg.endMm.x - seg.startMm.x);
  const dy = Math.abs(seg.endMm.y - seg.startMm.y);
  const m = Math.max(dx, dy);
  return m > 0.1 && Math.min(dx, dy) <= m * tol;
}

function segDir(seg: CadSegment): { x: number; y: number; len: number } {
  const dx = seg.endMm.x - seg.startMm.x;
  const dy = seg.endMm.y - seg.startMm.y;
  const len = Math.hypot(dx, dy) || 1;
  return { x: dx / len, y: dy / len, len };
}

function absDot(ax: number, ay: number, bx: number, by: number): number {
  return Math.abs(ax * bx + ay * by);
}

/** Prefer cut/panel layers over outline/swing (REV-148). */
export function layerQuality(layer?: string): number {
  const l = (layer ?? "").toLowerCase();
  if (l.includes("mcut")) return 30;
  if (l.includes("fram")) return 20;
  if (l.includes("otln")) return 5;
  return 10;
}

/**
 * Door leaf candidates: ortho segments in door-width range.
 * Pairs double lines (OTLN/MCUT faces) into a centerline leaf.
 * Ignores long swing arms that are perpendicular to the true opening.
 * When MCUT/FRAM leaf segments exist, OTLN-only candidates are ignored (REV-148).
 */
export function detectOpeningLeaves(
  segments: CadSegment[],
  kind: OpeningKind,
  options: {
    minOpeningWidthMm?: number;
    maxOpeningWidthMm?: number;
    leafPairGapMm?: number;
  } = {}
): DetectedOpening[] {
  const minW = options.minOpeningWidthMm ?? 600;
  const maxW = options.maxOpeningWidthMm ?? 2500;
  const pairGap = options.leafPairGapMm ?? 80;

  type LeafSeg = CadSegment & { _len: number; _dir: { x: number; y: number } };
  let candidates: LeafSeg[] = [];
  for (const seg of segments) {
    if (!isOrthoSeg(seg)) continue;
    const L = segmentLength(seg);
    // Leaf width band — exclude short ticks and long facade runs
    if (L < minW * 0.85 || L > maxW * 1.15) continue;
    const d = segDir(seg);
    candidates.push({ ...seg, _len: L, _dir: { x: d.x, y: d.y } });
  }

  // Prefer panel cut layers when present — OTLN often includes swing arms.
  if (kind === "door") {
    const preferred = candidates.filter((s) => layerQuality(s.layer) >= 20);
    if (preferred.length > 0) candidates = preferred;
  }

  const used = new Set<number>();
  const leaves: DetectedOpening[] = [];

  for (let i = 0; i < candidates.length; i++) {
    if (used.has(i)) continue;
    const a = candidates[i];
    let partner = -1;
    let bestGap = Infinity;
    for (let j = i + 1; j < candidates.length; j++) {
      if (used.has(j)) continue;
      const b = candidates[j];
      if (absDot(a._dir.x, a._dir.y, b._dir.x, b._dir.y) < 0.95) continue;
      // Midpoint distance ≈ leaf thickness (double line)
      const amx = (a.startMm.x + a.endMm.x) / 2;
      const amy = (a.startMm.y + a.endMm.y) / 2;
      const bmx = (b.startMm.x + b.endMm.x) / 2;
      const bmy = (b.startMm.y + b.endMm.y) / 2;
      const gap = dist(amx, amy, bmx, bmy);
      // Along-leaf midpoints should nearly coincide when projected
      const along =
        Math.abs((bmx - amx) * a._dir.x + (bmy - amy) * a._dir.y);
      if (along > Math.max(a._len, b._len) * 0.35) continue;
      if (gap >= 5 && gap <= pairGap && gap < bestGap) {
        bestGap = gap;
        partner = j;
      }
    }

    used.add(i);
    let center: PointMm;
    let widthMm: number;
    let bbox: BboxMm;
    let sourceCadIds: string[];
    let layer: string | undefined;
    let segmentCount: number;
    let leafDir = a._dir;

    if (partner >= 0) {
      used.add(partner);
      const b = candidates[partner];
      center = {
        x: (a.startMm.x + a.endMm.x + b.startMm.x + b.endMm.x) / 4,
        y: (a.startMm.y + a.endMm.y + b.startMm.y + b.endMm.y) / 4,
        z: 0,
      };
      widthMm = Math.round((a._len + b._len) / 2);
      bbox = bboxOfSegments([a, b]);
      sourceCadIds = [a.cadId, b.cadId].filter(Boolean) as string[];
      layer = a.layer ?? b.layer;
      segmentCount = 2;
    } else {
      center = {
        x: (a.startMm.x + a.endMm.x) / 2,
        y: (a.startMm.y + a.endMm.y) / 2,
        z: 0,
      };
      widthMm = Math.round(a._len);
      bbox = bboxOfSegments([a]);
      sourceCadIds = a.cadId ? [a.cadId] : [];
      layer = a.layer;
      segmentCount = 1;
    }

    leaves.push({
      kind,
      centerMm: center,
      widthMm: Math.max(minW, Math.min(maxW, widthMm)),
      layer,
      sourceCadIds,
      bboxMm: bbox,
      segmentCount,
      leafDir,
    });
  }

  return leaves;
}

/**
 * REV-149: rebuild door swing arcs from CAD.
 *
 * Every chord tessellated from one arc carries the same arcId plus the arc centre and
 * radius, so the swing survives the trip through Revit's geometry API intact. This
 * replaces guessing the swing side from segment midpoints.
 */
export function extractArcSwings(
  segments: CadSegment[],
  options: {
    minOpeningWidthMm?: number;
    maxOpeningWidthMm?: number;
    blocks?: CadBlock[];
  } = {}
): ArcSwing[] {
  const minW = options.minOpeningWidthMm ?? 600;
  const maxW = options.maxOpeningWidthMm ?? 2500;
  const blocksByIndex = new Map<number, CadBlock>();
  for (const b of options.blocks ?? []) blocksByIndex.set(b.index, b);

  const seen = new Map<string, ArcSwing>();
  for (const seg of segments) {
    const arcId = seg.arcId;
    const center = seg.arcCenterMm;
    const radius = seg.arcRadiusMm;
    if (!arcId || !center || !radius) continue;
    if (seen.has(arcId)) continue;
    // Swing radius IS the door leaf width — that alone rejects furniture arcs.
    if (radius < minW * 0.85 || radius > maxW * 1.15) continue;
    if (seg.arcStartAngleDeg == null || seg.arcEndAngleDeg == null) continue;

    const a0 = (seg.arcStartAngleDeg * Math.PI) / 180;
    const a1 = (seg.arcEndAngleDeg * Math.PI) / 180;
    const block =
      seg.blockIndex != null && seg.blockIndex >= 0
        ? blocksByIndex.get(seg.blockIndex)
        : undefined;

    seen.set(arcId, {
      arcId,
      hingeMm: { x: center.x, y: center.y, z: 0 },
      radiusMm: radius,
      endAMm: {
        x: center.x + radius * Math.cos(a0),
        y: center.y + radius * Math.sin(a0),
        z: 0,
      },
      endBMm: {
        x: center.x + radius * Math.cos(a1),
        y: center.y + radius * Math.sin(a1),
        z: 0,
      },
      layer: seg.layer,
      blockIndex: seg.blockIndex,
      blockName: block?.name,
      blockMirrored: block?.mirrored,
    });
  }

  return [...seen.values()];
}

export function openingsFromArcSwings(
  swings: ArcSwing[],
  kind: OpeningKind
): DetectedOpening[] {
  return swings.map((s) => {
    const xs = [s.hingeMm.x, s.endAMm.x, s.endBMm.x];
    const ys = [s.hingeMm.y, s.endAMm.y, s.endBMm.y];
    return {
      kind,
      // The hinge sits on the wall line, so it is the right point to measure host
      // proximity with. The true opening centre needs the wall direction first.
      centerMm: { ...s.hingeMm },
      widthMm: Math.round(s.radiusMm),
      layer: s.layer,
      sourceCadIds: [s.arcId],
      bboxMm: {
        minX: Math.min(...xs),
        minY: Math.min(...ys),
        maxX: Math.max(...xs),
        maxY: Math.max(...ys),
      },
      segmentCount: 1,
      arc: s,
      detection: "arc" as const,
    };
  });
}

/**
 * Decide which arc endpoint is the closed leaf (the one lying along the wall) and derive
 * the opening centre, width, swing side and hinge side from it.
 * Returns null when neither endpoint lines up with the wall — then this arc is not a
 * door on this wall.
 */
export function resolveArcSwingOnWall(
  arc: ArcSwing,
  wallDir: { x: number; y: number },
  alignMin = 0.85
): ResolvedArcSwing | null {
  const candidates: Array<{ closed: PointMm; open: PointMm }> = [
    { closed: arc.endAMm, open: arc.endBMm },
    { closed: arc.endBMm, open: arc.endAMm },
  ];

  let best: { closed: PointMm; open: PointMm; align: number } | null = null;
  for (const c of candidates) {
    const dx = c.closed.x - arc.hingeMm.x;
    const dy = c.closed.y - arc.hingeMm.y;
    const len = Math.hypot(dx, dy) || 1;
    const align = absDot(dx / len, dy / len, wallDir.x, wallDir.y);
    if (align < alignMin) continue;
    if (!best || align > best.align) best = { ...c, align };
  }
  if (!best) return null;

  const leafVx = best.closed.x - arc.hingeMm.x;
  const leafVy = best.closed.y - arc.hingeMm.y;
  const leafLen = Math.hypot(leafVx, leafVy) || 1;
  const leafDir = { x: leafVx / leafLen, y: leafVy / leafLen };

  const openVx = best.open.x - arc.hingeMm.x;
  const openVy = best.open.y - arc.hingeMm.y;
  // Strip any along-wall component so the normal is exactly perpendicular.
  const alongOpen = openVx * leafDir.x + openVy * leafDir.y;
  const nx = openVx - alongOpen * leafDir.x;
  const ny = openVy - alongOpen * leafDir.y;
  const nLen = Math.hypot(nx, ny);
  if (nLen < 1) return null; // degenerate: both endpoints along the wall
  const swingNormal = { x: nx / nLen, y: ny / nLen };

  const centerMm: PointMm = {
    x: (arc.hingeMm.x + best.closed.x) / 2,
    y: (arc.hingeMm.y + best.closed.y) / 2,
    z: 0,
  };

  return {
    centerMm,
    widthMm: Math.round(leafLen),
    leafDir,
    swingNormal,
    hingeMm: { ...arc.hingeMm },
    // Hinge lies half a leaf away from the centre, opposite the closed tip.
    hingeDir: { x: -leafDir.x, y: -leafDir.y },
  };
}

export function openingsFromClusters(
  clusters: CadSegment[][],
  kind: OpeningKind,
  options: {
    minOpeningWidthMm?: number;
    maxOpeningWidthMm?: number;
    minSegmentsPerCluster?: number;
  } = {}
): DetectedOpening[] {
  const minW = options.minOpeningWidthMm ?? 600;
  const maxW = options.maxOpeningWidthMm ?? 2500;
  const out: DetectedOpening[] = [];

  for (const cluster of clusters) {
    // Prefer leaf detection inside each cluster (ignores longer swing arms)
    const leaves = detectOpeningLeaves(cluster, kind, {
      minOpeningWidthMm: minW,
      maxOpeningWidthMm: maxW,
    });
    if (leaves.length > 0) {
      // One door symbol → one leaf (largest / most segments)
      leaves.sort(
        (a, b) =>
          b.segmentCount - a.segmentCount ||
          Math.abs(a.widthMm - 900) - Math.abs(b.widthMm - 900)
      );
      out.push(leaves[0]);
      continue;
    }

    if (cluster.length < (options.minSegmentsPerCluster ?? 1)) continue;
    const bbox = bboxOfSegments(cluster);
    const spanX = bbox.maxX - bbox.minX;
    const spanY = bbox.maxY - bbox.minY;
    if (Math.max(spanX, spanY) < minW * 0.4) continue;
    const maxSpan = Math.max(maxW * 2.5, 4500);
    if (Math.max(spanX, spanY) > maxSpan) continue;

    out.push({
      kind,
      centerMm: {
        x: (bbox.minX + bbox.maxX) / 2,
        y: (bbox.minY + bbox.maxY) / 2,
        z: 0,
      },
      widthMm: estimateOpeningWidthMm(bbox, minW, maxW),
      layer: cluster[0]?.layer,
      sourceCadIds: cluster.map((s) => s.cadId ?? "").filter(Boolean),
      bboxMm: bbox,
      segmentCount: cluster.length,
    });
  }
  return out;
}

export function dedupeOpenings(
  openings: DetectedOpening[],
  distanceMm = 400
): DetectedOpening[] {
  const kept: DetectedOpening[] = [];
  const used = new Set<number>();
  for (let i = 0; i < openings.length; i++) {
    if (used.has(i)) continue;
    let best = openings[i];
    for (let j = i + 1; j < openings.length; j++) {
      if (used.has(j)) continue;
      const d = dist(
        best.centerMm.x,
        best.centerMm.y,
        openings[j].centerMm.x,
        openings[j].centerMm.y
      );
      if (d <= distanceMm) {
        used.add(j);
        // Prefer MCUT / paired leaves / width nearer typical door
        const score = (o: DetectedOpening) =>
          layerQuality(o.layer) * 100 +
          o.segmentCount * 1000 -
          Math.abs(o.widthMm - 900);
        if (score(openings[j]) > score(best)) best = openings[j];
      }
    }
    kept.push(best);
  }
  return kept;
}

/**
 * Drop open-swing arms: when two leaves meet at a hinge and are perpendicular,
 * keep the closed panel (typically the shorter / more typical door width).
 */
export function suppressSwingLeaves(
  openings: DetectedOpening[]
): DetectedOpening[] {
  if (openings.length < 2) return openings;
  const drop = new Set<number>();

  for (let i = 0; i < openings.length; i++) {
    if (drop.has(i)) continue;
    const a = openings[i];
    if (!a.leafDir) continue;
    for (let j = i + 1; j < openings.length; j++) {
      if (drop.has(j)) continue;
      const b = openings[j];
      if (!b.leafDir) continue;
      if (absDot(a.leafDir.x, a.leafDir.y, b.leafDir.x, b.leafDir.y) > 0.35) {
        continue; // not perpendicular
      }
      // Hinge: corners within ~120 mm
      const aCorners = openingCorners(a);
      const bCorners = openingCorners(b);
      let hinge = false;
      for (const pa of aCorners) {
        for (const pb of bCorners) {
          if (dist(pa.x, pa.y, pb.x, pb.y) <= 120) {
            hinge = true;
            break;
          }
        }
        if (hinge) break;
      }
      if (!hinge) continue;

      // Closed leaf in wall is usually the shorter of leaf vs open swing arm.
      // Also prefer width nearer 900 when lengths are within 5%.
      const lenDiff = Math.abs(a.widthMm - b.widthMm);
      if (lenDiff > a.widthMm * 0.05 || lenDiff > 20) {
        if (a.widthMm <= b.widthMm) drop.add(j);
        else drop.add(i);
      } else {
        const score = (o: DetectedOpening) =>
          -Math.abs(o.widthMm - 900) + o.segmentCount * 5;
        if (score(a) >= score(b)) drop.add(j);
        else drop.add(i);
      }
    }
  }

  return openings.filter((_, i) => !drop.has(i));
}

function openingCorners(o: DetectedOpening): PointMm[] {
  const b = o.bboxMm;
  return [
    { x: b.minX, y: b.minY },
    { x: b.maxX, y: b.minY },
    { x: b.minX, y: b.maxY },
    { x: b.maxX, y: b.maxY },
  ];
}

/**
 * Infer swing side from nearby arc / short segments relative to each leaf.
 * swingNormal points from leaf center toward the open/swing geometry.
 */
export function annotateSwingNormals(
  openings: DetectedOpening[],
  segments: CadSegment[],
  searchRadiusMm = 900
): DetectedOpening[] {
  return openings.map((o) => {
    if (!o.leafDir) return o;
    // Perpendicular candidates (two opposite normals)
    const n1 = { x: -o.leafDir.y, y: o.leafDir.x };
    const n2 = { x: o.leafDir.y, y: -o.leafDir.x };
    let score1 = 0;
    let score2 = 0;

    for (const seg of segments) {
      const mx = (seg.startMm.x + seg.endMm.x) / 2;
      const my = (seg.startMm.y + seg.endMm.y) / 2;
      const d = dist(mx, my, o.centerMm.x, o.centerMm.y);
      if (d < 20 || d > searchRadiusMm) continue;
      const vx = mx - o.centerMm.x;
      const vy = my - o.centerMm.y;
      const along = vx * o.leafDir.x + vy * o.leafDir.y;
      if (Math.abs(along) > o.widthMm * 0.6) continue;
      const s1 = vx * n1.x + vy * n1.y;
      const s2 = vx * n2.x + vy * n2.y;
      const isArc = (seg.curveType ?? "").toLowerCase().includes("arc");
      const w = isArc ? 3 : 1;
      if (s1 > 30) score1 += w;
      if (s2 > 30) score2 += w;
    }

    if (score1 === 0 && score2 === 0) return o;
    const swingNormal = score1 >= score2 ? n1 : n2;
    return { ...o, swingNormal };
  });
}

export function traceOpeningsFromCad(
  segments: CadSegment[],
  options: TraceOpeningsOptions
): {
  openings: DetectedOpening[];
  stats: {
    inputSegments: number;
    filteredSegments: number;
    clusters: number;
    openings: number;
    excluded: number;
    /** REV-149: how the openings were found — arc is exact, leaf/cluster are heuristics. */
    detection: "arc" | "leaf" | "cluster" | "none";
    arcSwings: number;
  };
} {
  const patterns =
    options.layerPatterns ??
    (options.kind === "door"
      ? DEFAULT_DOOR_LAYER_PATTERNS
      : DEFAULT_WINDOW_LAYER_PATTERNS);
  const exclude = options.excludeLayers ?? DEFAULT_EXCLUDE_OPENING_LAYERS;
  const filtered = filterSegmentsForOpenings(segments, {
    layerPatterns: patterns,
    excludeLayers: exclude,
    bboxMm: options.bboxMm,
  });

  // Primary (REV-149): real swing arcs — hinge, width, swing side and hand come straight
  // from the DWG. Doors only; windows have no swing.
  let detection: "arc" | "leaf" | "cluster" | "none" = "none";
  let arcSwings: ArcSwing[] = [];
  let openings: DetectedOpening[] = [];

  if (options.kind === "door" && options.useArcs !== false) {
    arcSwings = extractArcSwings(filtered.segments, {
      minOpeningWidthMm: options.minOpeningWidthMm,
      maxOpeningWidthMm: options.maxOpeningWidthMm,
      blocks: options.blocks,
    });
    if (arcSwings.length > 0) {
      openings = openingsFromArcSwings(arcSwings, options.kind);
      // Two chords of the same swing can survive as separate arcs in odd DWGs.
      openings = dedupeOpenings(openings, options.dedupeDistanceMm ?? 400);
      detection = "arc";
    }
  }

  // Fallback: leaf segments (door panel in wall), not swing arcs
  if (openings.length === 0) {
    openings = detectOpeningLeaves(filtered.segments, options.kind, {
      minOpeningWidthMm: options.minOpeningWidthMm,
      maxOpeningWidthMm: options.maxOpeningWidthMm,
    });
    openings = suppressSwingLeaves(openings);
    openings = dedupeOpenings(openings, options.dedupeDistanceMm ?? 400);
    openings = annotateSwingNormals(openings, filtered.segments);
    if (openings.length > 0) detection = "leaf";
  }

  let clusters = 0;
  if (openings.length === 0) {
    // Fallback: cluster symbols (windows / odd CAD)
    const grouped = clusterOpeningSegments(
      filtered.segments,
      options.clusterGapMm ?? 450
    );
    clusters = grouped.length;
    openings = openingsFromClusters(grouped, options.kind, {
      minOpeningWidthMm: options.minOpeningWidthMm,
      maxOpeningWidthMm: options.maxOpeningWidthMm,
      minSegmentsPerCluster: options.minSegmentsPerCluster,
    });
    openings = dedupeOpenings(openings, options.dedupeDistanceMm ?? 400);
    if (openings.length > 0) detection = "cluster";
  }

  return {
    openings,
    stats: {
      inputSegments: segments.length,
      filteredSegments: filtered.segments.length,
      clusters,
      openings: openings.length,
      excluded: filtered.stats.excluded,
      detection,
      arcSwings: arcSwings.length,
    },
  };
}

export type PointOnWall = {
  point: PointMm;
  t: number;
  distanceMm: number;
  /** Parametric t before clamping to [0,1]. */
  tRaw?: number;
  /** Distance along wall from unclamped foot to clamped point. */
  alongOvershootMm?: number;
};

/** Project point onto infinite line of wall segment, clamped to [0,1]. */
export function projectPointOntoWall(
  point: PointMm,
  wall: HostWall
): PointOnWall {
  const ax = wall.startMm.x;
  const ay = wall.startMm.y;
  const bx = wall.endMm.x;
  const by = wall.endMm.y;
  const dx = bx - ax;
  const dy = by - ay;
  const len2 = dx * dx + dy * dy;
  if (len2 < 1e-6) {
    return {
      point: { x: ax, y: ay, z: point.z ?? 0 },
      t: 0,
      tRaw: 0,
      alongOvershootMm: 0,
      distanceMm: dist(point.x, point.y, ax, ay),
    };
  }
  const len = Math.sqrt(len2);
  const tRaw = ((point.x - ax) * dx + (point.y - ay) * dy) / len2;
  const t = Math.max(0, Math.min(1, tRaw));
  const px = ax + t * dx;
  const py = ay + t * dy;
  const alongOvershootMm = Math.abs(tRaw - t) * len;
  // Perpendicular distance to the infinite line (not to clamped end).
  const ix = ax + tRaw * dx;
  const iy = ay + tRaw * dy;
  const perpMm = dist(point.x, point.y, ix, iy);
  return {
    point: { x: px, y: py, z: point.z ?? 0 },
    t,
    tRaw,
    alongOvershootMm: Math.round(alongOvershootMm * 10) / 10,
    distanceMm: perpMm,
  };
}

function wallUnitDir(wall: HostWall): { x: number; y: number; len: number } {
  const dx = wall.endMm.x - wall.startMm.x;
  const dy = wall.endMm.y - wall.startMm.y;
  const len = Math.hypot(dx, dy) || 1;
  return { x: dx / len, y: dy / len, len };
}

function wallsColinear(
  a: HostWall,
  b: HostWall,
  perpTolMm = 60,
  alignMin = 0.95
): boolean {
  const da = wallUnitDir(a);
  const db = wallUnitDir(b);
  if (absDot(da.x, da.y, db.x, db.y) < alignMin) return false;
  // Perp distance from b.start to infinite line of a
  const vx = b.startMm.x - a.startMm.x;
  const vy = b.startMm.y - a.startMm.y;
  const perp = Math.abs(vx * -da.y + vy * da.x);
  return perp <= perpTolMm;
}

function alongParam(wall: HostWall, p: PointMm): number {
  const d = wallUnitDir(wall);
  return (p.x - wall.startMm.x) * d.x + (p.y - wall.startMm.y) * d.y;
}

function pointOnLine(wall: HostWall, alongMm: number): PointMm {
  const d = wallUnitDir(wall);
  return {
    x: wall.startMm.x + d.x * alongMm,
    y: wall.startMm.y + d.y * alongMm,
    z: 0,
  };
}

/**
 * Find two colinear wall segments with a gap that contains the opening center.
 * Used for WC stalls where walls were traced as prostenki only (REV-148).
 */
export function matchOpeningToGapHost(
  opening: DetectedOpening,
  walls: HostWall[],
  options: MatchHostsOptions = {}
): PlannedOpening | null {
  const maxDist = options.maxHostDistanceMm ?? 250;
  const maxGap =
    options.maxGapMm ?? Math.max((opening.widthMm || 900) * 2.5, 1500);
  const minGap = Math.max((opening.widthMm || 900) * 0.45, 350);
  const leaf = opening.leafDir;

  type Ranked = {
    left: HostWall;
    right: HostWall;
    gapStart: number;
    gapEnd: number;
    perpMm: number;
    score: number;
  };
  let best: Ranked | null = null;

  for (let i = 0; i < walls.length; i++) {
    for (let j = i + 1; j < walls.length; j++) {
      const a = walls[i];
      const b = walls[j];
      if (!wallsColinear(a, b)) continue;

      const d = wallUnitDir(a);
      if (leaf && absDot(leaf.x, leaf.y, d.x, d.y) < 0.85) continue;

      const a0 = Math.min(alongParam(a, a.startMm), alongParam(a, a.endMm));
      const a1 = Math.max(alongParam(a, a.startMm), alongParam(a, a.endMm));
      const b0 = Math.min(alongParam(a, b.startMm), alongParam(a, b.endMm));
      const b1 = Math.max(alongParam(a, b.startMm), alongParam(a, b.endMm));

      let left = a;
      let right = b;
      let left1 = a1;
      let right0 = b0;
      if (b1 < a0) {
        left = b;
        right = a;
        left1 = b1;
        right0 = a0;
      } else if (a1 <= b0) {
        left1 = a1;
        right0 = b0;
      } else {
        continue; // overlap
      }

      const gap = right0 - left1;
      if (gap < minGap || gap > maxGap) continue;

      const oAlong = alongParam(a, opening.centerMm);
      if (oAlong < left1 - 20 || oAlong > right0 + 20) continue;

      const foot = pointOnLine(a, oAlong);
      const perpMm = dist(
        opening.centerMm.x,
        opening.centerMm.y,
        foot.x,
        foot.y
      );
      if (perpMm > maxDist) continue;

      const score = perpMm + Math.abs(gap - (opening.widthMm || 900)) * 0.05;
      if (!best || score < best.score) {
        best = {
          left,
          right,
          gapStart: left1,
          gapEnd: right0,
          perpMm,
          score,
        };
      }
    }
  }

  if (!best) return null;

  const d = wallUnitDir(best.left);
  const oAlong = alongParam(best.left, opening.centerMm);
  const locationMm = pointOnLine(best.left, oAlong);
  const half = Math.max((opening.widthMm || 900) / 2 + 50, 350);
  // Bridge slightly longer than leaf so Revit can host the door clear of ends
  const bridgeStart = pointOnLine(best.left, oAlong - half);
  const bridgeEnd = pointOnLine(best.left, oAlong + half);

  return {
    ...opening,
    widthMm:
      opening.widthMm ||
      refineWidthAlongWall(opening.bboxMm, best.left, 600, 2500),
    hostWallId: 0,
    locationMm,
    hostDistanceMm: Math.round(best.perpMm * 10) / 10,
    paramT: 0.5,
    alongOvershootMm: 0,
    bridge: {
      kind: "gap",
      startMm: bridgeStart,
      endMm: bridgeEnd,
      donorWallId: best.left.id,
      leftWallId: best.left.id,
      rightWallId: best.right.id,
    },
  };
}

/**
 * When opening sits past the end of a single parallel wall (no second side),
 * plan a stub host wall centered on the CAD leaf (REV-148).
 */
export function matchOpeningToStubHost(
  opening: DetectedOpening,
  walls: HostWall[],
  options: MatchHostsOptions = {}
): PlannedOpening | null {
  const maxDist = options.maxHostDistanceMm ?? 250;
  const maxAlong = options.maxGapMm ?? Math.max((opening.widthMm || 900) * 2.5, 1500);
  const minAlong = options.maxAlongOvershootMm ?? 80;
  const leaf = opening.leafDir;

  let best: {
    wall: HostWall;
    proj: PointOnWall;
    score: number;
  } | null = null;

  for (const wall of walls) {
    const d = wallUnitDir(wall);
    if (leaf && absDot(leaf.x, leaf.y, d.x, d.y) < 0.85) continue;
    const proj = projectPointOntoWall(opening.centerMm, wall);
    if (proj.distanceMm > maxDist) continue;
    const over = proj.alongOvershootMm ?? 0;
    if (over < minAlong || over > maxAlong) continue;
    const score = proj.distanceMm + over * 0.02;
    if (!best || score < best.score) best = { wall, proj, score };
  }
  if (!best) return null;

  const d = wallUnitDir(best.wall);
  const oAlong = alongParam(best.wall, opening.centerMm);
  const locationMm = pointOnLine(best.wall, oAlong);
  const half = Math.max((opening.widthMm || 900) / 2 + 50, 350);
  return {
    ...opening,
    widthMm:
      opening.widthMm ||
      refineWidthAlongWall(opening.bboxMm, best.wall, 600, 2500),
    hostWallId: 0,
    locationMm,
    hostDistanceMm: Math.round(best.proj.distanceMm * 10) / 10,
    paramT: 0.5,
    alongOvershootMm: best.proj.alongOvershootMm,
    bridge: {
      kind: "stub",
      startMm: pointOnLine(best.wall, oAlong - half),
      endMm: pointOnLine(best.wall, oAlong + half),
      donorWallId: best.wall.id,
    },
  };
}

/** Bake a wall-resolved arc swing back into a plain DetectedOpening. */
export function applyResolvedArc(
  opening: DetectedOpening,
  resolved: ResolvedArcSwing
): DetectedOpening {
  return {
    ...opening,
    centerMm: resolved.centerMm,
    widthMm: resolved.widthMm,
    leafDir: resolved.leafDir,
    swingNormal: resolved.swingNormal,
    hingeMm: resolved.hingeMm,
  };
}

/**
 * REV-149: host matching for arc-derived doors.
 *
 * The hinge lies on the wall line by construction, so it — not a cluster centroid — is
 * what we measure against the wall. Once the wall direction is known the arc tells us
 * the exact centre, width, swing side and hinge side.
 */
export function matchArcOpeningToHost(
  opening: DetectedOpening,
  walls: HostWall[],
  options: MatchHostsOptions = {}
): PlannedOpening | null {
  const arc = opening.arc;
  if (!arc) return null;
  const maxDist = options.maxHostDistanceMm ?? 250;
  const maxAlong = options.maxAlongOvershootMm ?? 80;
  const allowBridge = options.allowBridge !== false;

  let best: {
    wall: HostWall;
    resolved: ResolvedArcSwing;
    proj: PointOnWall;
    score: number;
  } | null = null;

  for (const wall of walls) {
    const wdir = wallUnitDir(wall);
    const resolved = resolveArcSwingOnWall(arc, wdir);
    if (!resolved) continue;

    const hingeProj = projectPointOntoWall(arc.hingeMm, wall);
    if (hingeProj.distanceMm > maxDist) continue;

    const centerProj = projectPointOntoWall(resolved.centerMm, wall);
    if (centerProj.distanceMm > maxDist) continue;
    if ((centerProj.alongOvershootMm ?? 0) > maxAlong) continue;

    // The whole leaf must sit on this wall, else it belongs to a gap/stub bridge.
    const len = wallLength(wall);
    const halfN = len > 1e-6 ? resolved.widthMm / 2 / len : 1;
    if (centerProj.t - halfN < -1e-6 || centerProj.t + halfN > 1 + 1e-6) continue;

    const score = hingeProj.distanceMm + centerProj.distanceMm;
    if (!best || score < best.score) best = { wall, resolved, proj: centerProj, score };
  }

  if (best) {
    const resolvedOpening = applyResolvedArc(opening, best.resolved);
    return {
      ...resolvedOpening,
      hostWallId: best.wall.id,
      locationMm: best.proj.point,
      hostDistanceMm: Math.round(best.proj.distanceMm * 10) / 10,
      paramT: Math.round(best.proj.t * 1000) / 1000,
      alongOvershootMm: best.proj.alongOvershootMm ?? 0,
      hingeDir: best.resolved.hingeDir,
    };
  }

  if (!allowBridge) return null;

  // No continuous host: resolve the arc against each distinct wall direction and let the
  // gap/stub matchers work on a plain opening (WC stalls traced as prostenki only).
  const tried = new Set<string>();
  for (const wall of walls) {
    const wdir = wallUnitDir(wall);
    const key = `${Math.round(Math.abs(wdir.x) * 100)}:${Math.round(Math.abs(wdir.y) * 100)}`;
    if (tried.has(key)) continue;
    tried.add(key);

    const resolved = resolveArcSwingOnWall(arc, wdir);
    if (!resolved) continue;
    const plain = applyResolvedArc(opening, resolved);

    const viaGap =
      matchOpeningToGapHost(plain, walls, options) ??
      matchOpeningToStubHost(plain, walls, options);
    if (viaGap) return { ...viaGap, hingeDir: resolved.hingeDir };
  }

  return null;
}

/**
 * Pick host wall within maxHostDistanceMm.
 * Prefer walls parallel to the door leaf (same orientation as CAD panel).
 * Reject end-snaps far from CAD center — those become gap/stub bridges (REV-148).
 */
export function matchOpeningToHost(
  opening: DetectedOpening,
  walls: HostWall[],
  options: MatchHostsOptions = {}
): PlannedOpening | null {
  // REV-149: exact arc geometry beats every heuristic below.
  if (opening.arc) return matchArcOpeningToHost(opening, walls, options);

  const maxDist = options.maxHostDistanceMm ?? 250;
  const minClear = options.minEndClearanceMm ?? 100;
  const prefClear = options.preferredEndClearanceMm ?? 600;
  const maxAlong = options.maxAlongOvershootMm ?? 80;
  const leaf = opening.leafDir;
  const allowBridge = options.allowBridge !== false;

  let best: {
    wall: HostWall;
    proj: PointOnWall;
    score: number;
  } | null = null;

  for (const wall of walls) {
    const len = wallLength(wall);
    if (len < (opening.widthMm || 600) + minClear * 0.5) continue;
    const wdir = wallUnitDir(wall);

    // Leaf must be parallel to wall (door panel sits in wall)
    if (leaf) {
      const align = absDot(leaf.x, leaf.y, wdir.x, wdir.y);
      if (align < 0.85) continue;
    }

    const proj = projectPointOntoWall(opening.centerMm, wall);
    if (proj.distanceMm > maxDist) continue;
    // Far past the segment end → not an on-segment host
    if ((proj.alongOvershootMm ?? 0) > maxAlong) continue;

    const endDist = Math.min(proj.t, 1 - proj.t) * len;
    let score = proj.distanceMm;
    if (endDist < minClear) score += 200;
    else if (endDist < prefClear) score += (prefClear - endDist) * 0.15;
    score -= Math.min(len, 8000) * 0.001;
    if (leaf) {
      score -= absDot(leaf.x, leaf.y, wdir.x, wdir.y) * 50;
    }

    if (!best || score < best.score) {
      best = { wall, proj, score };
    }
  }

  if (best) {
    const widthMm =
      opening.widthMm ||
      refineWidthAlongWall(opening.bboxMm, best.wall, 600, 2500);

    // Final guard: CAD center must stay close to placement (honest underlay match)
    const cadToLoc = dist(
      opening.centerMm.x,
      opening.centerMm.y,
      best.proj.point.x,
      best.proj.point.y
    );
    if (cadToLoc <= maxDist + 30) {
      return {
        ...opening,
        widthMm,
        hostWallId: best.wall.id,
        locationMm: best.proj.point,
        hostDistanceMm: Math.round(best.proj.distanceMm * 10) / 10,
        paramT: Math.round(best.proj.t * 1000) / 1000,
        alongOvershootMm: best.proj.alongOvershootMm ?? 0,
      };
    }
  }

  if (!allowBridge) return null;

  return (
    matchOpeningToGapHost(opening, walls, options) ??
    matchOpeningToStubHost(opening, walls, options)
  );
}

export function matchOpeningsToHosts(
  openings: DetectedOpening[],
  walls: HostWall[],
  options: MatchHostsOptions = {}
): { planned: PlannedOpening[]; unmatched: DetectedOpening[] } {
  const planned: PlannedOpening[] = [];
  const unmatched: DetectedOpening[] = [];
  for (const o of openings) {
    const m = matchOpeningToHost(o, walls, options);
    if (m) planned.push(m);
    else unmatched.push(o);
  }
  return { planned, unmatched };
}

/** Parse width from door/window type name, e.g. "900×2100" or "Дверь 910". */
export function parseOpeningTypeWidthMm(name: string): number | null {
  if (!name) return null;
  const patterns = [
    /(\d{3,4})\s*[x×хX]\s*\d{3,4}/,
    /(\d+(?:[.,]\d+)?)\s*мм/i,
    /(\d+(?:[.,]\d+)?)\s*mm/i,
    /(?:^|[^\d])(\d{3,4})(?:\s|$)/,
  ];
  for (const re of patterns) {
    const m = name.match(re);
    if (m) {
      const v = Number(String(m[1]).replace(",", "."));
      if (Number.isFinite(v) && v >= 400 && v <= 3000) return v;
    }
  }
  return null;
}

export function matchOpeningTypeByWidth(
  widthMm: number,
  types: OpeningTypeCandidate[],
  tolMm = 80
): OpeningTypeCandidate | null {
  let best: OpeningTypeCandidate | null = null;
  let bestDiff = Infinity;
  for (const t of types) {
    const tw = t.widthMm ?? parseOpeningTypeWidthMm(t.name);
    if (tw == null) continue;
    const diff = Math.abs(tw - widthMm);
    if (diff < bestDiff && diff <= tolMm) {
      bestDiff = diff;
      best = { ...t, widthMm: tw };
    }
  }
  return best;
}

export type VerifyOpeningItem = {
  index: number;
  hostWallId: number;
  plannedCenterMm: PointMm;
  locationMm: PointMm;
  /** Distance CAD leaf center → placement (honest underlay check, REV-148). */
  deviationMm: number;
  /** Perp distance to host wall line (informational). */
  hostPerpMm?: number;
  ok: boolean;
  bridge?: boolean;
};

/**
 * Verify planned openings stay on the CAD leaf (not just somewhere on a host wall).
 * deviationMm = XY distance from CAD centerMm to locationMm (REV-148).
 */
export function verifyOpeningsAgainstHosts(
  planned: PlannedOpening[],
  walls: HostWall[],
  toleranceMm = 100
): {
  ok: boolean;
  maxDeviationMm: number;
  items: VerifyOpeningItem[];
  failedCount: number;
} {
  const byId = new Map(walls.map((w) => [w.id, w]));
  const items: VerifyOpeningItem[] = [];
  let maxDev = 0;

  for (let i = 0; i < planned.length; i++) {
    const p = planned[i];
    const cadToLoc = dist(
      p.centerMm.x,
      p.centerMm.y,
      p.locationMm.x,
      p.locationMm.y
    );
    const wall =
      p.hostWallId > 0
        ? byId.get(p.hostWallId)
        : p.bridge
          ? byId.get(p.bridge.donorWallId)
          : undefined;
    const hostPerpMm = wall
      ? projectPointOntoWall(p.centerMm, wall).distanceMm
      : p.hostDistanceMm;
    const ok = cadToLoc <= toleranceMm;
    maxDev = Math.max(maxDev, cadToLoc);
    items.push({
      index: i,
      hostWallId: p.hostWallId,
      plannedCenterMm: p.centerMm,
      locationMm: p.locationMm,
      deviationMm: Math.round(cadToLoc * 10) / 10,
      hostPerpMm: Math.round(hostPerpMm * 10) / 10,
      ok,
      bridge: !!p.bridge,
    });
  }

  const failedCount = items.filter((it) => !it.ok).length;
  return {
    ok: failedCount === 0,
    maxDeviationMm: Math.round(maxDev * 10) / 10,
    items,
    failedCount,
  };
}

export { computeSegmentsBbox };
