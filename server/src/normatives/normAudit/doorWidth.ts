/**
 * Model-side door-width helpers for the door_clear_width checker (REV-56).
 *
 * v1 compares the NOMINAL door parameter width (DOOR_WIDTH / openingWidthMm)
 * against a minimum from the norm library. Clear opening «в свету» (leaf minus
 * frame) is a separate follow-up — see runner warnings.
 */

/**
 * Family/type name fragments that mark door accessories (slopes, trims) rather
 * than real door blocks. Mirrors commandset OpeningFillClassifier (REV-41) so
 * откосы never count as doors in violations, even without a plugin rebuild.
 */
export const DOOR_ACCESSORY_KEYWORDS: readonly string[] = [
  "откос",
  "обвязк",
  "наличник",
  "добор",
  "reveal",
  "door trim",
  "jamb trim",
];

/** True when the family/type name indicates a door accessory, not a door leaf. */
export function isDoorAccessory(family?: string, type?: string): boolean {
  const text = `${family ?? ""} ${type ?? ""}`.toLowerCase();
  if (!text.trim()) return false;
  return DOOR_ACCESSORY_KEYWORDS.some((keyword) => text.includes(keyword));
}

export interface DoorWidthInput {
  id: number;
  uniqueId?: string;
  family?: string;
  type?: string;
  level?: string;
  openingWidthMm?: number | null;
  isOnEgressPath?: boolean;
}

export type DoorWidthStatus = "violation" | "nearLimit" | "compliant";

export interface ClassifiedDoor {
  id: number;
  uniqueId?: string;
  family: string;
  type: string;
  level: string;
  status: DoorWidthStatus;
  actualMm: number;
  requiredMm: number;
  /** Positive shortfall in mm (0 for compliant). */
  deviationMm: number;
  isOnEgressPath: boolean;
}

export interface DoorWidthClassification {
  violations: ClassifiedDoor[];
  nearLimit: ClassifiedDoor[];
  compliant: ClassifiedDoor[];
  /** Door blocks after the accessory (откос) filter. */
  totalDoors: number;
  /** Doors actually compared against the norm (egress + width present). */
  egressChecked: number;
  /** Accessories (откосы) removed before checking. */
  accessoriesSkipped: number;
  /** Doors on egress path but without a readable width. */
  missingWidth: number;
  /** Doors not on an egress path — no applicable interior-door width norm. */
  nonEgressSkipped: number;
}

export interface ClassifyDoorWidthOptions {
  minWidthMm: number;
  /** Shortfalls within this many mm are «nearLimit» (default 50). */
  nearLimitToleranceMm?: number;
  /**
   * When true (default) only doors on an egress path are compared — interior
   * apartment doors (bathroom 0.6–0.7 m) have no single applicable minimum and
   * must NOT be flagged (avoids REV-54-style false positives).
   */
  egressOnly?: boolean;
}

/**
 * Classify door widths against a minimum. Pure — no Revit / DB access, so the
 * golden fixtures (narrow / ok / откос / non-egress) test it directly.
 */
export function classifyDoorWidths(
  doors: DoorWidthInput[],
  options: ClassifyDoorWidthOptions
): DoorWidthClassification {
  const tolerance = options.nearLimitToleranceMm ?? 50;
  const egressOnly = options.egressOnly ?? true;
  const minWidthMm = options.minWidthMm;

  const violations: ClassifiedDoor[] = [];
  const nearLimit: ClassifiedDoor[] = [];
  const compliant: ClassifiedDoor[] = [];

  let totalDoors = 0;
  let accessoriesSkipped = 0;
  let missingWidth = 0;
  let nonEgressSkipped = 0;

  for (const door of doors) {
    if (isDoorAccessory(door.family, door.type)) {
      accessoriesSkipped += 1;
      continue;
    }
    totalDoors += 1;

    if (egressOnly && !door.isOnEgressPath) {
      nonEgressSkipped += 1;
      continue;
    }

    const width = door.openingWidthMm;
    if (width == null || !Number.isFinite(width) || width <= 0) {
      missingWidth += 1;
      continue;
    }

    const deviationMm = minWidthMm - width;
    const classified: ClassifiedDoor = {
      id: door.id,
      uniqueId: door.uniqueId,
      family: door.family ?? "",
      type: door.type ?? "",
      level: door.level ?? "",
      status: "compliant",
      actualMm: width,
      requiredMm: minWidthMm,
      deviationMm: deviationMm > 0 ? deviationMm : 0,
      isOnEgressPath: Boolean(door.isOnEgressPath),
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
    totalDoors,
    egressChecked: violations.length + nearLimit.length + compliant.length,
    accessoriesSkipped,
    missingWidth,
    nonEgressSkipped,
  };
}
