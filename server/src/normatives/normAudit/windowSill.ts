/**
 * Model-side window sill helpers for window_sill_height (REV-58).
 * Compares INSTANCE_SILL_HEIGHT / sillHeightMm against a minimum from the
 * norm library. Accessories (подоконник family as fill, откос) are excluded.
 */

export const WINDOW_ACCESSORY_KEYWORDS: readonly string[] = [
  "откос",
  "подоконник",
  "слив",
  "reveal",
  "window sill",
  "drip",
];

/** True when family/type name indicates a window accessory, not a window block. */
export function isWindowAccessory(family?: string, type?: string): boolean {
  const text = `${family ?? ""} ${type ?? ""}`.toLowerCase();
  if (!text.trim()) return false;
  return WINDOW_ACCESSORY_KEYWORDS.some((keyword) => text.includes(keyword));
}

export interface WindowSillInput {
  id: number;
  uniqueId?: string;
  family?: string;
  type?: string;
  level?: string;
  category?: string;
  sillHeightMm?: number | null;
}

export type WindowSillStatus = "violation" | "nearLimit" | "compliant";

export interface ClassifiedWindowSill {
  id: number;
  uniqueId?: string;
  family: string;
  type: string;
  level: string;
  status: WindowSillStatus;
  actualMm: number;
  requiredMm: number;
  /** Positive shortfall in mm (0 for compliant). */
  deviationMm: number;
}

export interface WindowSillClassification {
  violations: ClassifiedWindowSill[];
  nearLimit: ClassifiedWindowSill[];
  compliant: ClassifiedWindowSill[];
  totalWindows: number;
  checked: number;
  accessoriesSkipped: number;
  missingSill: number;
  nonWindowsSkipped: number;
}

export interface ClassifyWindowSillOptions {
  /** Minimum sill height from floor (mm). */
  minSillHeightMm: number;
  nearLimitToleranceMm?: number;
}

/**
 * Classify window sill heights against a minimum. Pure — golden-testable.
 * Violation when actual < required (sill too low).
 */
export function classifyWindowSills(
  openings: WindowSillInput[],
  options: ClassifyWindowSillOptions
): WindowSillClassification {
  const tolerance = options.nearLimitToleranceMm ?? 50;
  const minMm = options.minSillHeightMm;

  const violations: ClassifiedWindowSill[] = [];
  const nearLimit: ClassifiedWindowSill[] = [];
  const compliant: ClassifiedWindowSill[] = [];

  let totalWindows = 0;
  let accessoriesSkipped = 0;
  let missingSill = 0;
  let nonWindowsSkipped = 0;

  for (const item of openings) {
    const category = (item.category ?? "window").toLowerCase();
    if (category !== "window") {
      nonWindowsSkipped += 1;
      continue;
    }

    if (isWindowAccessory(item.family, item.type)) {
      accessoriesSkipped += 1;
      continue;
    }
    totalWindows += 1;

    const sill = item.sillHeightMm;
    if (sill == null || !Number.isFinite(sill) || sill < 0) {
      missingSill += 1;
      continue;
    }

    const deviationMm = minMm - sill;
    const classified: ClassifiedWindowSill = {
      id: item.id,
      uniqueId: item.uniqueId,
      family: item.family ?? "",
      type: item.type ?? "",
      level: item.level ?? "",
      status: "compliant",
      actualMm: sill,
      requiredMm: minMm,
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
    totalWindows,
    checked: violations.length + nearLimit.length + compliant.length,
    accessoriesSkipped,
    missingSill,
    nonWindowsSkipped,
  };
}
