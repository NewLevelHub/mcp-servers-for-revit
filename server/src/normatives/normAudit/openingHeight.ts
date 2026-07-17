/**
 * Model-side opening-height helpers for opening_height (REV-58).
 * v1 compares nominal DOOR_HEIGHT / WINDOW_HEIGHT against a minimum
 * (typically эвак. выход в свету ≥ 1,9 м). Default: doors on egress only.
 */

import { isDoorAccessory } from "./doorWidth.js";
import { isWindowAccessory } from "./windowSill.js";

export interface OpeningHeightInput {
  id: number;
  uniqueId?: string;
  family?: string;
  type?: string;
  level?: string;
  category?: string;
  openingHeightMm?: number | null;
  isOnEgressPath?: boolean;
}

export type OpeningHeightStatus = "violation" | "nearLimit" | "compliant";

export interface ClassifiedOpeningHeight {
  id: number;
  uniqueId?: string;
  family: string;
  type: string;
  level: string;
  category: string;
  status: OpeningHeightStatus;
  actualMm: number;
  requiredMm: number;
  deviationMm: number;
  isOnEgressPath: boolean;
}

export interface OpeningHeightClassification {
  violations: ClassifiedOpeningHeight[];
  nearLimit: ClassifiedOpeningHeight[];
  compliant: ClassifiedOpeningHeight[];
  totalOpenings: number;
  checked: number;
  accessoriesSkipped: number;
  missingHeight: number;
  nonEgressSkipped: number;
  windowsSkipped: number;
}

export interface ClassifyOpeningHeightOptions {
  minHeightMm: number;
  nearLimitToleranceMm?: number;
  /**
   * When true (default) only doors on an egress path are compared — the
   * typical «высота эвакуационных выходов» rule does not apply to apartment
   * windows or interior non-egress doors.
   */
  egressDoorsOnly?: boolean;
}

/**
 * Classify opening heights against a minimum. Pure — golden-testable.
 */
export function classifyOpeningHeights(
  openings: OpeningHeightInput[],
  options: ClassifyOpeningHeightOptions
): OpeningHeightClassification {
  const tolerance = options.nearLimitToleranceMm ?? 50;
  const minMm = options.minHeightMm;
  const egressDoorsOnly = options.egressDoorsOnly ?? true;

  const violations: ClassifiedOpeningHeight[] = [];
  const nearLimit: ClassifiedOpeningHeight[] = [];
  const compliant: ClassifiedOpeningHeight[] = [];

  let totalOpenings = 0;
  let accessoriesSkipped = 0;
  let missingHeight = 0;
  let nonEgressSkipped = 0;
  let windowsSkipped = 0;

  for (const item of openings) {
    const category = (item.category ?? "door").toLowerCase();
    const isWindow = category === "window";
    const isDoor = category === "door";

    if (isWindow && isWindowAccessory(item.family, item.type)) {
      accessoriesSkipped += 1;
      continue;
    }
    if (isDoor && isDoorAccessory(item.family, item.type)) {
      accessoriesSkipped += 1;
      continue;
    }

    if (egressDoorsOnly) {
      if (isWindow) {
        windowsSkipped += 1;
        continue;
      }
      if (!item.isOnEgressPath) {
        nonEgressSkipped += 1;
        continue;
      }
    }

    totalOpenings += 1;

    const height = item.openingHeightMm;
    if (height == null || !Number.isFinite(height) || height <= 0) {
      missingHeight += 1;
      continue;
    }

    const deviationMm = minMm - height;
    const classified: ClassifiedOpeningHeight = {
      id: item.id,
      uniqueId: item.uniqueId,
      family: item.family ?? "",
      type: item.type ?? "",
      level: item.level ?? "",
      category,
      status: "compliant",
      actualMm: height,
      requiredMm: minMm,
      deviationMm: deviationMm > 0 ? deviationMm : 0,
      isOnEgressPath: Boolean(item.isOnEgressPath),
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
    totalOpenings,
    checked: violations.length + nearLimit.length + compliant.length,
    accessoriesSkipped,
    missingHeight,
    nonEgressSkipped,
    windowsSkipped,
  };
}
