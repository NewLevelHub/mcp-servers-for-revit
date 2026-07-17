/**
 * Room minimum area classification for REV-57.
 */

import {
  classifyResidentialRoom,
  type ResidentialRoomCategory,
} from "./roomPurpose.js";
import type { RoomAreaLimit } from "./resolveRoomAreaLimits.js";
import { limitForCategory } from "./resolveRoomAreaLimits.js";

export interface RoomAreaInput {
  id: number;
  uniqueId?: string;
  name?: string;
  number?: string;
  department?: string;
  level?: string;
  areaM2?: number | null;
}

export type RoomAreaStatus = "violation" | "nearLimit" | "compliant";

export interface ClassifiedRoomArea {
  id: number;
  uniqueId?: string;
  name: string;
  level: string;
  category: ResidentialRoomCategory;
  status: RoomAreaStatus;
  areaM2: number;
  requiredAreaM2: number;
  /** Shortfall in m² (0 if compliant). */
  deviationM2: number;
  limitSource: RoomAreaLimit;
}

export interface RoomAreaClassification {
  violations: ClassifiedRoomArea[];
  nearLimit: ClassifiedRoomArea[];
  compliant: ClassifiedRoomArea[];
  checked: number;
  skippedUnknown: number;
  skippedNoLimit: number;
  missingArea: number;
}

export interface ClassifyRoomAreaOptions {
  limits: RoomAreaLimit[];
  nearLimitToleranceM2?: number;
}

export function classifyRoomAreas(
  rooms: RoomAreaInput[],
  options: ClassifyRoomAreaOptions
): RoomAreaClassification {
  const tolerance = options.nearLimitToleranceM2 ?? 0.5;
  const violations: ClassifiedRoomArea[] = [];
  const nearLimit: ClassifiedRoomArea[] = [];
  const compliant: ClassifiedRoomArea[] = [];

  let checked = 0;
  let skippedUnknown = 0;
  let skippedNoLimit = 0;
  let missingArea = 0;

  for (const room of rooms) {
    const category = classifyResidentialRoom(room.name, room.department);
    if (category === "excluded" || category === "unknown") {
      skippedUnknown += 1;
      continue;
    }

    const limit = limitForCategory(options.limits, category);
    if (!limit) {
      skippedNoLimit += 1;
      continue;
    }

    const area = room.areaM2;
    if (area == null || !Number.isFinite(area) || area <= 0) {
      missingArea += 1;
      continue;
    }

    checked += 1;
    const deviationM2 = limit.minAreaM2 - area;
    const displayName =
      (room.name && room.name.trim()) ||
      (room.number && room.number.trim()) ||
      `помещение ${room.id}`;

    const classified: ClassifiedRoomArea = {
      id: room.id,
      uniqueId: room.uniqueId,
      name: displayName,
      level: room.level ?? "",
      category,
      status: "compliant",
      areaM2: area,
      requiredAreaM2: limit.minAreaM2,
      deviationM2: deviationM2 > 0 ? deviationM2 : 0,
      limitSource: limit,
    };

    if (deviationM2 <= 0) {
      compliant.push(classified);
    } else if (deviationM2 <= tolerance) {
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
    skippedUnknown,
    skippedNoLimit,
    missingArea,
  };
}
