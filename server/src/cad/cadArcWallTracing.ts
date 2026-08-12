/**
 * REV-154: curved wall tracing from DWG arcs.
 *
 * The straight-line tracer cannot see curved walls at all: a DWG arc arrives tessellated into
 * ~200 mm chords, which are dropped by minLengthMm long before pairing, and whatever survives is
 * killed by orthoOnly. On «Проект1» that silently lost half the plan.
 *
 * Reading the CAD with arcMode="single" instead gives one chord per arc carrying centre, radius
 * and the angular range, so the two faces of a curved wall are simply two concentric arcs whose
 * radii differ by the wall thickness. Pure geometry (mm); unit-tested without Revit.
 */

import type { BboxMm, CadSegment, PointMm } from "./cadWallTracing.js";

export type ArcWallAxis = {
  startMm: PointMm;
  endMm: PointMm;
  /** Third point Revit needs to rebuild the arc (Arc.Create takes ends + a point on the arc). */
  midMm: PointMm;
  centerMm: PointMm;
  radiusMm: number;
  startAngleDeg: number;
  endAngleDeg: number;
  /** Arc length along the centreline. */
  lengthMm: number;
  thicknessMm: number;
  sourceCadIds: string[];
};

export type ArcSkipReason =
  | "noConcentricPartner"
  | "gapTooNarrow"
  | "gapTooWide"
  | "tooShort";

export type SkippedArc = {
  reason: ArcSkipReason;
  centerMm: PointMm;
  radiusMm: number;
  startAngleDeg: number;
  endAngleDeg: number;
  lengthMm: number;
  layer?: string;
  cadId?: string;
  nearestConcentricGapMm?: number;
};

export type ArcTraceOptions = {
  minPairGapMm?: number;
  maxPairGapMm?: number;
  /** Min centreline arc length to keep (mm). */
  minWallLengthMm?: number;
  /** Max distance between two arc centres still considered concentric (mm). */
  centerToleranceMm?: number;
  /** Max chord gap between two arcs of one wall that still gets merged (mm). */
  mergeGapMm?: number;
  bboxMm?: BboxMm;
};

export type ArcTraceResult = {
  axes: ArcWallAxis[];
  skipped: SkippedArc[];
  stats: {
    inputArcs: number;
    pairedCount: number;
    afterMerge: number;
    unpairedSkipped: number;
    shortSkipped: number;
  };
};

type ArcFace = {
  centerMm: PointMm;
  radiusMm: number;
  /** Normalised so that end > start; both in degrees. */
  startAngleDeg: number;
  endAngleDeg: number;
  layer?: string;
  cadId?: string;
  used?: boolean;
};

const TWO_PI_DEG = 360;

function toRad(deg: number): number {
  return (deg * Math.PI) / 180;
}

function pointOnCircle(
  center: PointMm,
  radiusMm: number,
  angleDeg: number
): PointMm {
  const a = toRad(angleDeg);
  return {
    x: center.x + radiusMm * Math.cos(a),
    y: center.y + radiusMm * Math.sin(a),
    z: center.z ?? 0,
  };
}

function angleOf(center: PointMm, p: PointMm): number {
  return (Math.atan2(p.y - center.y, p.x - center.x) * 180) / Math.PI;
}

/**
 * Bring a range to end > start. DWG hands out angles in (-180, 180], so a sweep crossing the
 * -180/180 seam arrives with end < start.
 */
function normaliseRange(
  startDeg: number,
  endDeg: number
): { start: number; end: number } {
  let end = endDeg;
  while (end <= startDeg) end += TWO_PI_DEG;
  return { start: startDeg, end };
}

/** Angular overlap of two ranges, accounting for the 360° wrap. */
function overlapRange(
  a: { start: number; end: number },
  b: { start: number; end: number }
): { start: number; end: number } | null {
  for (const shift of [-TWO_PI_DEG, 0, TWO_PI_DEG]) {
    const start = Math.max(a.start, b.start + shift);
    const end = Math.min(a.end, b.end + shift);
    if (end > start) return { start, end };
  }
  return null;
}

/**
 * CAD segments → arc faces. Only segments that carry arc metadata take part; everything else
 * belongs to the straight-line tracer.
 */
export function extractArcFaces(segments: CadSegment[]): ArcFace[] {
  const faces: ArcFace[] = [];
  const seen = new Set<string>();

  for (const seg of segments) {
    const center = seg.arcCenterMm;
    const radius = seg.arcRadiusMm;
    if (!center || !radius || radius <= 0) continue;

    // arcMode="single" gives one chord per arc, but a tessellated read would repeat arcId per
    // chord — keep the first and derive the range from the metadata either way.
    const key = seg.arcId ?? seg.cadId ?? `${center.x}:${center.y}:${radius}`;
    if (seen.has(key)) continue;
    seen.add(key);

    const startDeg = seg.arcStartAngleDeg ?? angleOf(center, seg.startMm);
    const endDeg = seg.arcEndAngleDeg ?? angleOf(center, seg.endMm);
    const range = normaliseRange(startDeg, endDeg);
    if (range.end - range.start < 1e-6) continue;

    faces.push({
      centerMm: center,
      radiusMm: radius,
      startAngleDeg: range.start,
      endAngleDeg: range.end,
      layer: seg.layer,
      cadId: seg.cadId ?? seg.arcId,
    });
  }

  return faces;
}

function makeAxis(
  center: PointMm,
  radiusMm: number,
  startDeg: number,
  endDeg: number,
  thicknessMm: number,
  sourceCadIds: string[]
): ArcWallAxis {
  const midDeg = (startDeg + endDeg) / 2;
  const spanRad = toRad(endDeg - startDeg);
  return {
    startMm: pointOnCircle(center, radiusMm, startDeg),
    endMm: pointOnCircle(center, radiusMm, endDeg),
    midMm: pointOnCircle(center, radiusMm, midDeg),
    centerMm: center,
    radiusMm,
    startAngleDeg: startDeg,
    endAngleDeg: endDeg,
    lengthMm: radiusMm * spanRad,
    thicknessMm,
    sourceCadIds,
  };
}

function insideBbox(p: PointMm, bbox?: BboxMm): boolean {
  if (!bbox) return true;
  return (
    p.x >= bbox.minX && p.x <= bbox.maxX && p.y >= bbox.minY && p.y <= bbox.maxY
  );
}

/**
 * Merge arcs that continue one another: same centre, same radius, and an angular gap small
 * enough to be a drawing seam rather than an opening.
 */
function mergeAdjacentAxes(
  axes: ArcWallAxis[],
  mergeGapMm: number,
  centerToleranceMm: number
): ArcWallAxis[] {
  const sorted = [...axes].sort(
    (a, b) =>
      a.centerMm.x - b.centerMm.x ||
      a.centerMm.y - b.centerMm.y ||
      a.radiusMm - b.radiusMm ||
      a.startAngleDeg - b.startAngleDeg
  );

  const merged: ArcWallAxis[] = [];
  for (const axis of sorted) {
    const prev = merged[merged.length - 1];
    const sameCircle =
      prev &&
      Math.hypot(
        prev.centerMm.x - axis.centerMm.x,
        prev.centerMm.y - axis.centerMm.y
      ) <= centerToleranceMm &&
      Math.abs(prev.radiusMm - axis.radiusMm) <= centerToleranceMm;

    if (sameCircle) {
      const gapMm = toRad(axis.startAngleDeg - prev.endAngleDeg) * axis.radiusMm;
      if (gapMm <= mergeGapMm) {
        const end = Math.max(prev.endAngleDeg, axis.endAngleDeg);
        merged[merged.length - 1] = makeAxis(
          prev.centerMm,
          (prev.radiusMm + axis.radiusMm) / 2,
          prev.startAngleDeg,
          end,
          (prev.thicknessMm + axis.thicknessMm) / 2,
          [...prev.sourceCadIds, ...axis.sourceCadIds]
        );
        continue;
      }
    }
    merged.push(axis);
  }

  return merged;
}

export function traceArcWallAxesFromCad(
  segments: CadSegment[],
  options: ArcTraceOptions = {}
): ArcTraceResult {
  const minPairGapMm = options.minPairGapMm ?? 55;
  const maxPairGapMm = options.maxPairGapMm ?? 280;
  const minWallLengthMm = options.minWallLengthMm ?? 300;
  const centerToleranceMm = options.centerToleranceMm ?? 50;
  const mergeGapMm = options.mergeGapMm ?? 50;

  const faces = extractArcFaces(segments).filter((f) =>
    insideBbox(pointOnCircle(f.centerMm, f.radiusMm, f.startAngleDeg), options.bboxMm)
  );

  const skipped: SkippedArc[] = [];
  const axes: ArcWallAxis[] = [];

  // Every unordered pair, best (largest angular overlap) first — a face must not be consumed by
  // a worse partner just because it came first in the DWG.
  type Candidate = {
    a: number;
    b: number;
    overlap: { start: number; end: number };
    gapMm: number;
  };
  const candidates: Candidate[] = [];

  for (let i = 0; i < faces.length; i++) {
    for (let j = i + 1; j < faces.length; j++) {
      const a = faces[i];
      const b = faces[j];
      const centerDist = Math.hypot(
        a.centerMm.x - b.centerMm.x,
        a.centerMm.y - b.centerMm.y
      );
      if (centerDist > centerToleranceMm) continue;

      const gapMm = Math.abs(a.radiusMm - b.radiusMm);
      if (gapMm < minPairGapMm || gapMm > maxPairGapMm) continue;

      const overlap = overlapRange(
        { start: a.startAngleDeg, end: a.endAngleDeg },
        { start: b.startAngleDeg, end: b.endAngleDeg }
      );
      if (!overlap) continue;

      candidates.push({ a: i, b: j, overlap, gapMm });
    }
  }

  candidates.sort(
    (x, y) => y.overlap.end - y.overlap.start - (x.overlap.end - x.overlap.start)
  );

  let pairedCount = 0;
  for (const cand of candidates) {
    const a = faces[cand.a];
    const b = faces[cand.b];
    if (a.used || b.used) continue;
    a.used = true;
    b.used = true;
    pairedCount++;

    const radiusMm = (a.radiusMm + b.radiusMm) / 2;
    axes.push(
      makeAxis(
        a.centerMm,
        radiusMm,
        cand.overlap.start,
        cand.overlap.end,
        cand.gapMm,
        [a.cadId, b.cadId].filter((v): v is string => !!v)
      )
    );
  }

  let unpairedSkipped = 0;
  for (const face of faces) {
    if (face.used) continue;
    unpairedSkipped++;

    // Report why: a nearby concentric arc that missed the gap band is a parameter problem, no
    // partner at all is a drawing problem.
    let nearestGap: number | undefined;
    for (const other of faces) {
      if (other === face) continue;
      const centerDist = Math.hypot(
        face.centerMm.x - other.centerMm.x,
        face.centerMm.y - other.centerMm.y
      );
      if (centerDist > centerToleranceMm) continue;
      const gap = Math.abs(face.radiusMm - other.radiusMm);
      if (gap < 1e-6) continue;
      if (nearestGap == null || gap < nearestGap) nearestGap = gap;
    }

    const reason: ArcSkipReason =
      nearestGap == null
        ? "noConcentricPartner"
        : nearestGap < minPairGapMm
          ? "gapTooNarrow"
          : "gapTooWide";

    skipped.push({
      reason,
      centerMm: face.centerMm,
      radiusMm: face.radiusMm,
      startAngleDeg: face.startAngleDeg,
      endAngleDeg: face.endAngleDeg,
      lengthMm: face.radiusMm * toRad(face.endAngleDeg - face.startAngleDeg),
      layer: face.layer,
      cadId: face.cadId,
      nearestConcentricGapMm: nearestGap,
    });
  }

  const mergedAxes = mergeAdjacentAxes(axes, mergeGapMm, centerToleranceMm);

  let shortSkipped = 0;
  const kept: ArcWallAxis[] = [];
  for (const axis of mergedAxes) {
    if (axis.lengthMm < minWallLengthMm) {
      shortSkipped++;
      skipped.push({
        reason: "tooShort",
        centerMm: axis.centerMm,
        radiusMm: axis.radiusMm,
        startAngleDeg: axis.startAngleDeg,
        endAngleDeg: axis.endAngleDeg,
        lengthMm: axis.lengthMm,
      });
      continue;
    }
    kept.push(axis);
  }

  return {
    axes: kept,
    skipped,
    stats: {
      inputArcs: faces.length,
      pairedCount,
      afterMerge: mergedAxes.length,
      unpairedSkipped,
      shortSkipped,
    },
  };
}

/**
 * REV-154: turn arc skip reasons into the parameter change that would fix them.
 */
export function buildArcTraceHints(
  result: ArcTraceResult,
  opts: { minPairGapMm: number; maxPairGapMm: number }
): string[] {
  const hints: string[] = [];
  if (result.axes.length > 0) {
    hints.push(
      `${result.axes.length} криволинейн(ая/ых) стен(а/ы) собрана(ы) из дуг DWG ` +
        `(радиусы центральных линий ` +
        `${[...new Set(result.axes.map((a) => Math.round(a.radiusMm)))].join(", ")} мм).`
    );
  }

  const wide = result.skipped.filter((s) => s.reason === "gapTooWide");
  if (wide.length > 0) {
    const gaps = wide
      .map((s) => s.nearestConcentricGapMm)
      .filter((g): g is number => g != null)
      .sort((a, b) => a - b);
    const needed = Math.ceil((gaps[gaps.length - 1] + 20) / 10) * 10;
    hints.push(
      `${wide.length} дуг(и) не спарены: ближайшая концентрическая грань в ` +
        `${Math.round(gaps[0])}–${Math.round(gaps[gaps.length - 1])} мм при ` +
        `maxPairGapMm=${opts.maxPairGapMm}. Повторите с maxPairGapMm: ${needed}.`
    );
  }

  const alone = result.skipped.filter((s) => s.reason === "noConcentricPartner");
  if (alone.length > 0) {
    hints.push(
      `${alone.length} дуг(и) без второй грани: на подложке криволинейная стена нарисована ` +
        `одной линией — обведите её вручную или уточните layerFilter.`
    );
  }

  return hints;
}
