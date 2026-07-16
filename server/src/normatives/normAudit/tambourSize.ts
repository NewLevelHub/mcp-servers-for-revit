/**
 * Tambour / vestibule size helpers for REV-67.
 *
 * v1 compares bounding width × depth (mm) from room geometry against a minimum
 * side length from the norm library (typically 1650 mm per СП РК 3.02-101-2012
 * п. 4.4.10.6). Both sides must meet the minimum.
 */

/** Name fragments that identify a tambour / vestibule room in ADSK templates. */
export const TAMBOUR_NAME_KEYWORDS: readonly string[] = [
  "тамбур",
  "tambour",
  "vestibule",
  "тамбұр",
  "кіреберіс",
  "кiреберiс",
  "входной тамбур",
  "входной тамбұр",
];

export interface TambourRoomInput {
  id: number;
  uniqueId?: string;
  name?: string;
  number?: string;
  level?: string;
  widthMm?: number | null;
  depthMm?: number | null;
}

export type TambourSizeStatus = "violation" | "nearLimit" | "compliant";

export interface ClassifiedTambour {
  id: number;
  uniqueId?: string;
  name: string;
  level: string;
  status: TambourSizeStatus;
  widthMm: number;
  depthMm: number;
  /** min(width, depth) — the limiting side for the norm. */
  minSideMm: number;
  requiredSideMm: number;
  deviationMm: number;
}

export interface TambourSizeClassification {
  violations: ClassifiedTambour[];
  nearLimit: ClassifiedTambour[];
  compliant: ClassifiedTambour[];
  tamboursFound: number;
  missingGeometry: number;
}

export interface ClassifyTambourSizeOptions {
  minSideMm: number;
  nearLimitToleranceMm?: number;
}

/** True when room name/number looks like a tambour (not a generic corridor). */
export function isTambourRoom(name?: string, number?: string): boolean {
  const text = `${name ?? ""} ${number ?? ""}`.toLowerCase();
  if (!text.trim()) return false;
  return TAMBOUR_NAME_KEYWORDS.some((keyword) => text.includes(keyword));
}

/**
 * Classify tambour rooms by min(width, depth) vs required minimum side.
 * Pure — golden tests run without Revit.
 */
export function classifyTambourSizes(
  rooms: TambourRoomInput[],
  options: ClassifyTambourSizeOptions
): TambourSizeClassification {
  const tolerance = options.nearLimitToleranceMm ?? 50;
  const requiredSideMm = options.minSideMm;

  const violations: ClassifiedTambour[] = [];
  const nearLimit: ClassifiedTambour[] = [];
  const compliant: ClassifiedTambour[] = [];

  let tamboursFound = 0;
  let missingGeometry = 0;

  for (const room of rooms) {
    if (!isTambourRoom(room.name, room.number)) continue;
    tamboursFound += 1;

    const width = room.widthMm;
    const depth = room.depthMm;
    if (
      width == null ||
      depth == null ||
      !Number.isFinite(width) ||
      !Number.isFinite(depth) ||
      width <= 0 ||
      depth <= 0
    ) {
      missingGeometry += 1;
      continue;
    }

    const minSideMm = Math.min(width, depth);
    const deviationMm = requiredSideMm - minSideMm;
    const displayName =
      (room.name && room.name.trim()) ||
      (room.number && room.number.trim()) ||
      `помещение ${room.id}`;

    const classified: ClassifiedTambour = {
      id: room.id,
      uniqueId: room.uniqueId,
      name: displayName,
      level: room.level ?? "",
      status: "compliant",
      widthMm: width,
      depthMm: depth,
      minSideMm,
      requiredSideMm,
      deviationMm: deviationMm > 0 ? deviationMm : 0,
    };

    if (deviationMm <= 0) {
      classified.status = "compliant";
      compliant.push(classified);
    } else if (deviationMm <= tolerance) {
      classified.status = "nearLimit";
      nearLimit.push(classified);
    } else {
      classified.status = "violation";
      violations.push(classified);
    }
  }

  return {
    violations,
    nearLimit,
    compliant,
    tamboursFound,
    missingGeometry,
  };
}
