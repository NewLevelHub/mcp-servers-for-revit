/**
 * Room minimum clear-height classification for REV-57.
 * Prefer level-based clear height (see resolveRoomClearHeight) over raw UnboundedHeight.
 */

import {
  classifyResidentialRoom,
  isResidentialRoomForHeight,
} from "./roomPurpose.js";

export interface RoomHeightInput {
  id: number;
  uniqueId?: string;
  name?: string;
  number?: string;
  department?: string;
  level?: string;
  /** Resolved clear height to check against the norm (mm). */
  clearHeightMm?: number | null;
  /** @deprecated use clearHeightMm — kept for older call sites/tests */
  unboundedHeightMm?: number | null;
  heightSource?: string;
}

export type RoomHeightStatus = "violation" | "nearLimit" | "compliant";

export interface ClassifiedRoomHeight {
  id: number;
  uniqueId?: string;
  name: string;
  level: string;
  status: RoomHeightStatus;
  actualHeightMm: number;
  requiredHeightMm: number;
  deviationMm: number;
  heightSource?: string;
}

export interface RoomHeightClassification {
  violations: ClassifiedRoomHeight[];
  nearLimit: ClassifiedRoomHeight[];
  compliant: ClassifiedRoomHeight[];
  checked: number;
  skipped: number;
  missingHeight: number;
}

export function classifyRoomHeights(
  rooms: RoomHeightInput[],
  options: {
    minHeightMm: number;
    nearLimitToleranceMm?: number;
  }
): RoomHeightClassification {
  const tolerance = options.nearLimitToleranceMm ?? 50;
  const violations: ClassifiedRoomHeight[] = [];
  const nearLimit: ClassifiedRoomHeight[] = [];
  const compliant: ClassifiedRoomHeight[] = [];

  let checked = 0;
  let skipped = 0;
  let missingHeight = 0;

  for (const room of rooms) {
    const category = classifyResidentialRoom(room.name, room.department);
    if (!isResidentialRoomForHeight(category)) {
      skipped += 1;
      continue;
    }

    const height = room.clearHeightMm ?? room.unboundedHeightMm;
    if (height == null || !Number.isFinite(height) || height <= 0) {
      missingHeight += 1;
      continue;
    }

    checked += 1;
    const deviationMm = options.minHeightMm - height;
    const displayName =
      (room.name && room.name.trim()) ||
      (room.number && room.number.trim()) ||
      `помещение ${room.id}`;

    const classified: ClassifiedRoomHeight = {
      id: room.id,
      uniqueId: room.uniqueId,
      name: displayName,
      level: room.level ?? "",
      status: "compliant",
      actualHeightMm: height,
      requiredHeightMm: options.minHeightMm,
      deviationMm: deviationMm > 0 ? deviationMm : 0,
      heightSource: room.heightSource,
    };

    if (deviationMm <= 0) {
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
    checked,
    skipped,
    missingHeight,
  };
}
