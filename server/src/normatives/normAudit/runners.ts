import { z } from "zod";
import { withRevitConnection } from "../../utils/ConnectionManager.js";
import type { NormAuditRevitSnapshot } from "./auditSnapshot.js";
import {
  DEFAULT_EVACUATION_WIDTH_PDF_FILES,
  loadEvacuationWidthRulesFromNormatives,
  pickPrimaryEvacuationWidthRule,
} from "../evacuationWidthRules.js";
import {
  DEFAULT_MIN_DIMENSIONS_PDF_FILES,
  loadMinDimensionRulesFromNormatives,
  resolveMinDimensionLimits,
  type MinDimensionNormRule,
} from "../minDimensionsRules.js";
import { applyFireDoorRules } from "../applyFireDoorRules.js";
import {
  DEFAULT_FIRE_DOOR_PDF_FILES,
  loadFireDoorRulesFromNormatives,
} from "../fireDoorRules.js";
import {
  classifyDoorWidths,
  type ClassifiedDoor,
  type DoorWidthInput,
} from "./doorWidth.js";
import {
  classifyWindowSills,
  type ClassifiedWindowSill,
  type WindowSillInput,
} from "./windowSill.js";
import {
  classifyOpeningHeights,
  type ClassifiedOpeningHeight,
  type OpeningHeightInput,
} from "./openingHeight.js";
import {
  classifyRamps,
  classifyRailingHeights,
  classifyStairRiserTreads,
  classifyStairWidths,
  type ClassifiedRamp,
  type ClassifiedRailing,
  type ClassifiedStairRiserTread,
  type ClassifiedStairWidth,
} from "./verticalCirculation.js";
import {
  classifyTambourSizes,
  type ClassifiedTambour,
  type TambourRoomInput,
} from "./tambourSize.js";
import {
  classifyAccessibilityRamps,
  classifyDoorManeuvering,
  classifyAccessibilityRooms,
  MGN_DOOR_SOURCE,
  MGN_DOOR_WIDTH_MM,
  type AccessibilityRoomInput,
  type AccessibilityDoorManeuveringInput,
  type ClassifiedAccessibilityRamp,
  type ClassifiedAccessibilityRoom,
  type ClassifiedDoorManeuvering,
} from "./accessibility.js";
import {
  classifyRoomAreas,
  type ClassifiedRoomArea,
  type RoomAreaInput,
} from "./roomArea.js";
import {
  classifyRoomHeights,
  type ClassifiedRoomHeight,
  type RoomHeightInput,
} from "./roomHeight.js";
import {
  resolveRoomClearHeight,
  DEFAULT_FLOOR_THICKNESS_MM,
  type LevelElevation,
} from "./resolveRoomClearHeight.js";
import {
  classifyStoreyHeights,
  type ClassifiedStoreyHeight,
  type LevelInput,
} from "./storeyHeight.js";
import type { RoomAreaLimit } from "./resolveRoomAreaLimits.js";
import type { NormAuditFinding, NormAuditSource } from "./types.js";
import { toAuditSource } from "./types.js";

function classifyNearLimit<T extends { isCompliant: boolean; deviationMm: number }>(
  items: T[],
  toleranceMm: number
): { nearLimit: T[]; violations: T[] } {
  const nearLimit: T[] = [];
  const violations: T[] = [];
  for (const item of items) {
    if (item.isCompliant) continue;
    if (item.deviationMm > 0 && item.deviationMm <= toleranceMm) {
      nearLimit.push(item);
    } else {
      violations.push(item);
    }
  }
  return { nearLimit, violations };
}

const evacuationItemSchema = z.object({
  id: z.number(),
  uniqueId: z.string().optional(),
  name: z.string().optional().default(""),
  number: z.string().optional().default(""),
  level: z.string().optional().default(""),
  actualWidthMm: z.number(),
  requiredWidthMm: z.number(),
  deviationMm: z.number(),
  isCompliant: z.boolean(),
});

export interface EvacuationWidthRunnerResult {
  success: boolean;
  message: string;
  minWidthMm: number;
  totalChecked: number;
  violations: z.infer<typeof evacuationItemSchema>[];
  nearLimit: z.infer<typeof evacuationItemSchema>[];
  compliant: z.infer<typeof evacuationItemSchema>[];
  source: NormAuditSource;
  warnings: string[];
}

export async function runEvacuationWidthCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  nearLimitToleranceMm?: number;
}): Promise<EvacuationWidthRunnerResult> {
  const { rules, warnings, normativesDir } =
    await loadEvacuationWidthRulesFromNormatives({});
  const appliedRule = pickPrimaryEvacuationWidthRule(rules);
  const minWidthMm = appliedRule?.minWidthMm;
  if (minWidthMm === undefined) {
    return {
      success: false,
      message:
        "Не удалось определить minWidthMm из normatives/. Передайте норму вручную через check_evacuation_width.",
      minWidthMm: 0,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: toAuditSource(null),
      warnings: [
        ...(warnings ?? []),
        normativesDir ? `Каталог: ${normativesDir}` : "normatives/ недоступен",
      ],
    };
  }

  const rawResponse = await withRevitConnection(async (revitClient) => {
    return await revitClient.sendCommand("check_evacuation_width", {
      minWidthMm,
      mode: "report",
      levelName: options.levelName,
      roomNameFilter: "",
      corridorOnly: true,
      includeCompliant: options.includeCompliant,
      highlightTarget: "violations",
    });
  });

  const raw = z
    .object({
      success: z.boolean(),
      message: z.string(),
      totalCorridorsChecked: z.number().optional(),
      violations: z.array(evacuationItemSchema).optional(),
      compliantCorridors: z.array(evacuationItemSchema).optional(),
    })
    .parse(rawResponse);

  if (!raw.success) {
    return {
      success: false,
      message: raw.message,
      minWidthMm,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: toAuditSource(appliedRule?.source),
      warnings: warnings ?? [],
    };
  }

  const tolerance = options.nearLimitToleranceMm ?? 100;
  const { nearLimit, violations } = classifyNearLimit(
    raw.violations ?? [],
    tolerance
  );

  return {
    success: true,
    message: raw.message,
    minWidthMm,
    totalChecked: raw.totalCorridorsChecked ?? 0,
    violations,
    nearLimit,
    compliant: raw.compliantCorridors ?? [],
    source: toAuditSource(appliedRule?.source),
    warnings: [
      ...(warnings ?? []),
      `PDF по умолчанию: ${DEFAULT_EVACUATION_WIDTH_PDF_FILES.join(", ")}`,
    ],
  };
}

const roomDepthItemSchema = z.object({
  id: z.number(),
  uniqueId: z.string().optional(),
  name: z.string().optional().default(""),
  number: z.string().optional().default(""),
  level: z.string().optional().default(""),
  depthMm: z.number(),
  isCompliant: z.boolean(),
  deviationMm: z.number().optional(),
});

export interface RoomDepthRunnerResult {
  success: boolean;
  message: string;
  totalChecked: number;
  violations: z.infer<typeof roomDepthItemSchema>[];
  compliant: z.infer<typeof roomDepthItemSchema>[];
  minDepthMm?: number;
  maxDepthMm?: number;
  source: NormAuditSource;
  warnings: string[];
}

export async function runRoomDepthCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  minDepthMm?: number;
  maxDepthMm?: number;
  source: NormAuditSource;
}): Promise<RoomDepthRunnerResult> {
  if (options.minDepthMm == null && options.maxDepthMm == null) {
    return {
      success: false,
      message:
        "Нет лимита глубины в библиотеке норм. Сделайте seed / query «глубина помещения».",
      totalChecked: 0,
      violations: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }

  const rawResponse = await withRevitConnection(async (revitClient) => {
    return await revitClient.sendCommand("check_room_depth", {
      minDepthMm: options.minDepthMm,
      maxDepthMm: options.maxDepthMm,
      mode: "report",
      levelName: options.levelName,
      roomScope: "living",
      roomNameFilter: "",
      includeCompliant: options.includeCompliant,
    });
  });

  const raw = z
    .object({
      success: z.boolean(),
      message: z.string(),
      totalRoomsChecked: z.number().optional(),
      violations: z.array(roomDepthItemSchema).optional(),
      compliantRooms: z.array(roomDepthItemSchema).optional(),
    })
    .parse(rawResponse);

  if (!raw.success) {
    return {
      success: false,
      message: raw.message,
      totalChecked: 0,
      violations: [],
      compliant: [],
      minDepthMm: options.minDepthMm,
      maxDepthMm: options.maxDepthMm,
      source: options.source,
      warnings: [],
    };
  }

  return {
    success: true,
    message: raw.message,
    totalChecked: raw.totalRoomsChecked ?? 0,
    violations: raw.violations ?? [],
    compliant: raw.compliantRooms ?? [],
    minDepthMm: options.minDepthMm,
    maxDepthMm: options.maxDepthMm,
    source: options.source,
    warnings: [],
  };
}

const minDimItemSchema = z.object({
  id: z.number(),
  uniqueId: z.string().optional(),
  name: z.string().optional().default(""),
  number: z.string().optional().default(""),
  level: z.string().optional().default(""),
  spaceKind: z.string().optional().default(""),
  metric: z.string().optional().default(""),
  actualValueMm: z.number(),
  requiredValueMm: z.number(),
  deviationMm: z.number(),
  isCompliant: z.boolean(),
});

export interface MinDimensionsRunnerResult {
  success: boolean;
  message: string;
  totalChecked: number;
  violations: z.infer<typeof minDimItemSchema>[];
  compliant: z.infer<typeof minDimItemSchema>[];
  appliedRules: MinDimensionNormRule[];
  fallbackSource: NormAuditSource;
  warnings: string[];
}

export async function runMinDimensionsCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  housingType?: "ordinary" | "mgn";
}): Promise<MinDimensionsRunnerResult> {
  const housingType = options.housingType ?? "ordinary";
  const { rules, warnings } = await loadMinDimensionRulesFromNormatives({});
  const limits = resolveMinDimensionLimits(rules, { housingType });
  const allWarnings = [...(warnings ?? [])];
  if (limits.skippedMgnRules > 0 && housingType === "ordinary") {
    allWarnings.push(
      `Обычное жильё: не применялись ${limits.skippedMgnRules} правил(а) МГН/престарелых (1,4 м п. 4.6.5 / 3.06-101) к квартирным лоджиям. ` +
        `п. 4.2.30 (1,2 м) — только воздушная зона / путь к Н1. Для МГН: housingType=mgn.`
    );
  }
  if (limits.measurementNote) {
    allWarnings.push(limits.measurementNote);
  }
  const hasLimit =
    limits.minBalconyWidthMm !== undefined ||
    limits.minLoggiaWidthMm !== undefined ||
    limits.minLoggiaDepthMm !== undefined ||
    limits.minFirePathOutdoorWidthMm !== undefined ||
    limits.minFirePierToOpeningMm !== undefined ||
    limits.minFirePierBetweenOpeningsMm !== undefined;

  if (!hasLimit) {
    return {
      success: true,
      message:
        "Для обычного жилья нет применимой мин. ширины квартирных лоджий; простенки и путь к Н1 тоже не извлечены.",
      totalChecked: 0,
      violations: [],
      compliant: [],
      appliedRules: limits.appliedRules,
      fallbackSource: toAuditSource(null),
      warnings: allWarnings,
    };
  }

  const rawResponse = await withRevitConnection(async (revitClient) => {
    return await revitClient.sendCommand("check_min_dimensions", {
      minBalconyWidthMm: limits.minBalconyWidthMm,
      minLoggiaWidthMm: limits.minLoggiaWidthMm,
      minLoggiaDepthMm: limits.minLoggiaDepthMm,
      minFirePathOutdoorWidthMm: limits.minFirePathOutdoorWidthMm,
      minFirePierToOpeningMm: limits.minFirePierToOpeningMm,
      minFirePierBetweenOpeningsMm: limits.minFirePierBetweenOpeningsMm,
      mode: "report",
      levelName: options.levelName,
      roomNameFilter: "",
      includeCompliant: options.includeCompliant,
      checkFirePiers: true,
    });
  });

  const raw = z
    .object({
      success: z.boolean(),
      message: z.string(),
      totalSpacesChecked: z.number().optional(),
      violations: z.array(minDimItemSchema).optional(),
      compliantItems: z.array(minDimItemSchema).optional(),
    })
    .parse(rawResponse);

  if (!raw.success) {
    return {
      success: false,
      message: raw.message,
      totalChecked: 0,
      violations: [],
      compliant: [],
      appliedRules: limits.appliedRules,
      fallbackSource: toAuditSource(limits.appliedRules[0]?.source),
      warnings: allWarnings,
    };
  }

  return {
    success: true,
    message: raw.message,
    totalChecked: raw.totalSpacesChecked ?? 0,
    violations: raw.violations ?? [],
    compliant: raw.compliantItems ?? [],
    appliedRules: limits.appliedRules,
    fallbackSource: toAuditSource(limits.appliedRules[0]?.source),
    warnings: [
      ...allWarnings,
      `PDF по умолчанию: ${DEFAULT_MIN_DIMENSIONS_PDF_FILES.join(", ")}`,
    ],
  };
}

const fireDoorSchema = z.object({
  id: z.number(),
  uniqueId: z.string().optional().default(""),
  mark: z.string().optional().default(""),
  family: z.string().optional().default(""),
  type: z.string().optional().default(""),
  level: z.string().optional().default(""),
  fromRoom: z.string().optional().default(""),
  toRoom: z.string().optional().default(""),
  openingWidthMm: z.number().nullable().optional(),
  isOnEgressPath: z.boolean(),
  isMarkedAsFireDoor: z.boolean(),
  markSource: z
    .enum(["none", "parameter", "schedule_note", "both"])
    .optional()
    .default("none"),
  currentFireRating: z.string().optional().default(""),
  scheduleNote: z.string().optional().default(""),
});

export interface FireDoorsRunnerResult {
  success: boolean;
  message: string;
  totalChecked: number;
  doors: Array<{
    id: number;
    uniqueId?: string;
    mark: string;
    type: string;
    family: string;
    level: string;
    fromRoom: string;
    toRoom: string;
    requiresFireDoor: boolean;
    compliant: boolean;
    reason: string;
    source: NormAuditSource;
    markSource?: string;
    openingWidthMm?: number | null;
  }>;
  warnings: string[];
}

export async function runFireDoorsCheck(options: {
  levelName: string;
}): Promise<FireDoorsRunnerResult> {
  const { rules, warnings } = await loadFireDoorRulesFromNormatives({});
  const rawResponse = await withRevitConnection(async (revitClient) => {
    return await revitClient.sendCommand("check_fire_doors", {
      levelName: options.levelName,
    });
  });

  const raw = z
    .object({
      success: z.boolean(),
      message: z.string(),
      totalDoors: z.number(),
      doors: z.array(fireDoorSchema),
      warnings: z.array(z.string()).optional().default([]),
    })
    .parse(rawResponse);

  if (!raw.success) {
    return {
      success: false,
      message: raw.message,
      totalChecked: 0,
      doors: [],
      warnings: [...(warnings ?? []), ...(raw.warnings ?? [])],
    };
  }

  const applied = applyFireDoorRules(
    raw.doors.map((door) => ({
      ...door,
      uniqueId: door.uniqueId || String(door.id),
    })),
    rules
  );
  return {
    success: true,
    message: applied.message,
    totalChecked: applied.totalDoors,
    doors: applied.doors.map((door) => ({
      id: door.id,
      uniqueId: door.uniqueId,
      mark: door.mark,
      type: door.type,
      family: door.family,
      level: door.level,
      fromRoom: door.fromRoom,
      toRoom: door.toRoom,
      requiresFireDoor: door.requiresFireDoor,
      compliant: door.compliant,
      reason: door.reason,
      source: toAuditSource(door.source),
      markSource: door.markSource,
      openingWidthMm: door.openingWidthMm,
    })),
    warnings: [
      ...(warnings ?? []),
      ...(raw.warnings ?? []),
      `PDF по умолчанию: ${DEFAULT_FIRE_DOOR_PDF_FILES.join(", ")}`,
    ],
  };
}

const doorEgressItemSchema = z.object({
  id: z.number(),
  uniqueId: z.string().optional().default(""),
  family: z.string().optional().default(""),
  type: z.string().optional().default(""),
  level: z.string().optional().default(""),
  openingWidthMm: z.number().nullable().optional(),
  clearWidthMm: z.number().nullable().optional(),
  widthSource: z.string().optional().default(""),
  isOnEgressPath: z.boolean().optional().default(false),
  maneuveringDepthMm: z.number().nullable().optional(),
  maneuveringWidthMm: z.number().nullable().optional(),
  maneuveringRoom: z.string().optional().default(""),
  maneuveringRequiredDepthMm: z.number().nullable().optional(),
  maneuveringApproach: z.string().optional().default(""),
});

const rampAccessibilityItemSchema = z.object({
  id: z.number(),
  uniqueId: z.string().optional().default(""),
  name: z.string().optional().default(""),
  level: z.string().optional().default(""),
  slopePercent: z.number().nullable().optional(),
  slopeSource: z.string().optional().default(""),
  riseMm: z.number().nullable().optional(),
  isExceptionAllowed: z.boolean().optional().default(false),
});

export interface DoorWidthRunnerResult {
  success: boolean;
  message: string;
  minWidthMm: number;
  /** Door blocks after the accessory (откос) filter. */
  totalChecked: number;
  violations: ClassifiedDoor[];
  nearLimit: ClassifiedDoor[];
  compliant: ClassifiedDoor[];
  unmeasured?: DoorWidthInput[];
  source: NormAuditSource;
  warnings: string[];
}

/**
 * Door clear-width check (REV-56).
 * Reads widths via get_door_egress_info, drops откосы (REV-41), and compares
 * only doors on an egress path against the resolved minimum.
 */
export async function runDoorWidthCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  minWidthMm: number;
  source: NormAuditSource;
  nearLimitToleranceMm?: number;
  egressOnly?: boolean;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<DoorWidthRunnerResult> {
  if (!(options.minWidthMm > 0)) {
    return {
      success: false,
      message:
        "Нет числовой нормы ширины двери. Сделайте seed / query «ширина эвакуационного выхода».",
      minWidthMm: 0,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      unmeasured: [],
      source: options.source,
      warnings: [],
    };
  }

  const rawResponse =
    options.snapshot?.doorEgressInfo ??
    (await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand("get_door_egress_info", {
        levelName: options.levelName,
      });
    }));

  const raw = z
    .object({
      success: z.boolean(),
      message: z.string().optional().default(""),
      totalDoors: z.number().optional(),
      doors: z.array(doorEgressItemSchema).optional().default([]),
    })
    .parse(rawResponse);

  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "get_door_egress_info failed.",
      minWidthMm: options.minWidthMm,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }

  const doors: DoorWidthInput[] = raw.doors.map((door) => ({
    id: door.id,
    uniqueId: door.uniqueId || String(door.id),
    family: door.family,
    type: door.type,
    level: door.level,
    openingWidthMm: door.openingWidthMm ?? null,
    clearWidthMm: door.clearWidthMm ?? null,
    widthSource: door.widthSource,
    isOnEgressPath: door.isOnEgressPath,
  }));

  const classified = classifyDoorWidths(doors, {
    minWidthMm: options.minWidthMm,
    nearLimitToleranceMm: options.nearLimitToleranceMm ?? 50,
    egressOnly: options.egressOnly ?? true,
    requireClearWidth: true,
  });

  const warnings: string[] = [
    "Сравнивается параметр ширины «в свету» семейства; для старых семейств без " +
      "такого параметра результат явно помечается как nominal_fallback.",
  ];
  if (classified.accessoriesSkipped > 0) {
    warnings.push(
      `Откосы/наличники исключены из проверки: ${classified.accessoriesSkipped} (REV-41).`
    );
  }
  if (classified.nonEgressSkipped > 0) {
    warnings.push(
      `Двери вне путей эвакуации не проверялись (нет применимой нормы ширины): ${classified.nonEgressSkipped}.`
    );
  }
  if (classified.missingWidth > 0) {
    warnings.push(
      `Дверей без читаемой ширины: ${classified.missingWidth} (пропущены).`
    );
  }

  return {
    success: true,
    message:
      `Проверено дверей на путях эвакуации: ${classified.egressChecked} ` +
      `(из ${classified.totalDoors} дверных блоков). Норма ≥ ${options.minWidthMm} мм.`,
    minWidthMm: options.minWidthMm,
    totalChecked: classified.egressChecked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    unmeasured: classified.unmeasured,
    source: options.source,
    warnings,
  };
}

const roomGeometryItemSchema = z.object({
  id: z.number(),
  uniqueId: z.string().optional().default(""),
  name: z.string().optional().default(""),
  number: z.string().optional().default(""),
  level: z.string().optional().default(""),
  widthMm: z.number(),
  depthMm: z.number(),
});

export interface TambourSizeRunnerResult {
  success: boolean;
  message: string;
  minSideMm: number;
  totalChecked: number;
  violations: ClassifiedTambour[];
  nearLimit: ClassifiedTambour[];
  compliant: ClassifiedTambour[];
  source: NormAuditSource;
  warnings: string[];
}

/**
 * Tambour / vestibule size check (REV-67).
 * Reads room geometry via get_room_geometry_metrics and compares min(width, depth)
 * against the resolved minimum side (typically 1650 mm).
 */
export async function runTambourSizeCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  minSideMm: number;
  source: NormAuditSource;
  nearLimitToleranceMm?: number;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<TambourSizeRunnerResult> {
  if (!(options.minSideMm > 0)) {
    return {
      success: false,
      message:
        "Нет числовой нормы габарита тамбура. Сделайте seed / query «тамбур 1.65».",
      minSideMm: 0,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }

  const rawResponse =
    options.snapshot?.roomGeometryMetrics ??
    (await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand("get_room_geometry_metrics", {
        levelName: options.levelName,
        includeUnplacedRooms: false,
      });
    }));

  const raw = z
    .object({
      success: z.boolean(),
      message: z.string().optional().default(""),
      rooms: z.array(roomGeometryItemSchema).optional().default([]),
    })
    .parse(rawResponse);

  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "get_room_geometry_metrics failed.",
      minSideMm: options.minSideMm,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }

  const rooms: TambourRoomInput[] = raw.rooms.map((room) => ({
    id: room.id,
    uniqueId: room.uniqueId || String(room.id),
    name: room.name,
    number: room.number,
    level: room.level,
    widthMm: room.widthMm,
    depthMm: room.depthMm,
  }));

  const classified = classifyTambourSizes(rooms, {
    minSideMm: options.minSideMm,
    nearLimitToleranceMm: options.nearLimitToleranceMm ?? 50,
  });

  const warnings: string[] = [
    "v1: сравнивается ограничивающий прямоугольник помещения (ширина × глубина по геометрии Revit). " +
      "Фактический габарит с учётом отделки и ниш может отличаться.",
  ];
  if (classified.missingGeometry > 0) {
    warnings.push(
      `Тамбуров без читаемой геометрии: ${classified.missingGeometry} (пропущены).`
    );
  }
  if (classified.tamboursFound === 0) {
    warnings.push(
      "Помещения с именем «тамбур» / vestibule на этаже не найдены — проверьте именование помещений."
    );
  }

  const totalChecked =
    classified.violations.length +
    classified.nearLimit.length +
    classified.compliant.length;

  return {
    success: true,
    message:
      `Найдено тамбуров: ${classified.tamboursFound}, проверено: ${totalChecked}. ` +
      `Норма ≥ ${options.minSideMm} × ${options.minSideMm} мм.`,
    minSideMm: options.minSideMm,
    totalChecked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    source: options.source,
    warnings,
  };
}

export interface AccessibilityRoomsRunnerResult {
  success: boolean;
  message: string;
  totalChecked: number;
  turning: ClassifiedAccessibilityRoom[];
  corridors: ClassifiedAccessibilityRoom[];
  wc: ClassifiedAccessibilityRoom[];
  warnings: string[];
}

export async function runAccessibilityRoomsCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  nearLimitToleranceMm?: number;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<AccessibilityRoomsRunnerResult> {
  const rawResponse =
    options.snapshot?.roomGeometryMetrics ??
    (await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand("get_room_geometry_metrics", {
        levelName: options.levelName,
        includeUnplacedRooms: false,
      });
    }));

  const raw = z
    .object({
      success: z.boolean(),
      message: z.string().optional().default(""),
      rooms: z.array(roomGeometryItemSchema).optional().default([]),
    })
    .parse(rawResponse);

  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "get_room_geometry_metrics failed.",
      totalChecked: 0,
      turning: [],
      corridors: [],
      wc: [],
      warnings: [],
    };
  }

  const rooms: AccessibilityRoomInput[] = raw.rooms.map((room) => ({
    id: room.id,
    uniqueId: room.uniqueId || String(room.id),
    name: room.name,
    number: room.number,
    level: room.level,
    widthMm: room.widthMm,
    depthMm: room.depthMm,
  }));

  const classified = classifyAccessibilityRooms(rooms, {
    nearLimitToleranceMm: options.nearLimitToleranceMm ?? 50,
  });
  const warnings: string[] = [
    "v1: сравнивается ограничивающий прямоугольник помещения — фактическая зона " +
      "разворота с учётом мебели/сантехники может отличаться.",
  ];
  if (classified.accessibleWcFound === 0) {
    warnings.push(
      "Санузлы с пометкой МГН/доступный/универсальный не найдены — габариты доступных " +
        "санузлов не проверялись (обычные квартирные санузлы норме 4.3.3.14 не подлежат)."
    );
  }
  if (classified.missingGeometry > 0) {
    warnings.push(
      `Помещений без читаемой геометрии: ${classified.missingGeometry} (пропущены).`
    );
  }

  const totalChecked =
    classified.turning.length + classified.corridors.length + classified.wc.length;
  return {
    success: true,
    message:
      `МГН-геометрия: тамбуров ${classified.tamboursFound}, коридоров ${classified.corridorsFound}, ` +
      `доступных санузлов ${classified.accessibleWcFound}; проверок выполнено ${totalChecked}.`,
    totalChecked,
    turning: classified.turning,
    corridors: classified.corridors,
    wc: classified.wc,
    warnings,
  };
}

export interface AccessibilityDoorsRunnerResult {
  success: boolean;
  message: string;
  minWidthMm: number;
  totalChecked: number;
  violations: ClassifiedDoor[];
  nearLimit: ClassifiedDoor[];
  compliant: ClassifiedDoor[];
  unmeasuredDoors?: DoorWidthInput[];
  ramps?: ClassifiedAccessibilityRamp[];
  maneuvering?: ClassifiedDoorManeuvering[];
  unmeasuredManeuvering?: AccessibilityDoorManeuveringInput[];
  source: NormAuditSource;
  warnings: string[];
}

export async function runAccessibilityDoorsCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  nearLimitToleranceMm?: number;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<AccessibilityDoorsRunnerResult> {
  const rawResponse =
    options.snapshot?.doorEgressInfo ??
    (await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand("get_door_egress_info", {
        levelName: options.levelName,
      });
    }));
  const raw = z
    .object({
      success: z.boolean(),
      message: z.string().optional().default(""),
      totalDoors: z.number().optional(),
      doors: z.array(doorEgressItemSchema).optional().default([]),
      ramps: z.array(rampAccessibilityItemSchema).optional().default([]),
    })
    .parse(rawResponse);

  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "get_door_egress_info failed.",
      minWidthMm: MGN_DOOR_WIDTH_MM,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      unmeasuredDoors: [],
      ramps: [],
      maneuvering: [],
      unmeasuredManeuvering: [],
      source: MGN_DOOR_SOURCE,
      warnings: [],
    };
  }

  const doors: DoorWidthInput[] = raw.doors.map((door) => ({
    id: door.id,
    uniqueId: door.uniqueId || String(door.id),
    family: door.family,
    type: door.type,
    level: door.level,
    openingWidthMm: door.openingWidthMm ?? null,
    clearWidthMm: door.clearWidthMm ?? null,
    widthSource: door.widthSource,
    isOnEgressPath: door.isOnEgressPath,
  }));
  const classified = classifyDoorWidths(doors, {
    minWidthMm: MGN_DOOR_WIDTH_MM,
    nearLimitToleranceMm: options.nearLimitToleranceMm ?? 50,
    egressOnly: true,
    requireClearWidth: true,
  });
  const ramps = classifyAccessibilityRamps(
    raw.ramps.map((ramp) => ({
      ...ramp,
      riseMm: ramp.riseMm,
      isExceptionAllowed: ramp.isExceptionAllowed,
    }))
  );
  const maneuveringInputs: AccessibilityDoorManeuveringInput[] = raw.doors.map((door) => ({
      id: door.id,
      uniqueId: door.uniqueId || String(door.id),
      family: door.family,
      type: door.type,
      level: door.level,
      isOnEgressPath: door.isOnEgressPath,
      maneuveringDepthMm: door.maneuveringDepthMm,
      maneuveringWidthMm: door.maneuveringWidthMm,
      maneuveringRoom: door.maneuveringRoom,
      maneuveringRequiredDepthMm: door.maneuveringRequiredDepthMm,
      maneuveringApproach: door.maneuveringApproach,
    }));
  const maneuvering = classifyDoorManeuvering(
    maneuveringInputs,
    options.nearLimitToleranceMm ?? 50
  );
  const nominalFallbackCount = classified.unmeasured.length;
  const warnings: string[] = [
    "Ширина «в свету» читается из параметра семейства; при его отсутствии явно " +
      "помечается номинальный fallback. Зона маневрирования измеряется до границ " +
      "помещений без учёта мебели и оборудования.",
  ];
  if (nominalFallbackCount > 0) {
    warnings.push(
      `Дверей без достоверной ширины «в свету»: ${nominalFallbackCount}; отмечены skipped, номинал не принят за факт.`
    );
  }
  if (classified.accessoriesSkipped > 0) {
    warnings.push(
      `Откосы/наличники исключены: ${classified.accessoriesSkipped} (REV-41).`
    );
  }
  if (classified.missingWidth > 0) {
    warnings.push(
      `Дверей без читаемой ширины: ${classified.missingWidth} (пропущены).`
    );
  }
  if (ramps.missingGeometry > 0) {
    warnings.push(`Пандусов без читаемого уклона: ${ramps.missingGeometry} (пропущены).`);
  }
  if (maneuvering.missingGeometry > 0) {
    warnings.push(
      `Дверей доступного пути без измеримой зоны маневрирования: ${maneuvering.missingGeometry} (пропущены).`
    );
  }

  return {
    success: true,
    message:
      `МГН: проверено дверей на доступных путях ${classified.egressChecked} ` +
      `(из ${classified.totalDoors} дверных блоков), пандусов ${ramps.findings.length}, ` +
      `зон маневрирования ${maneuvering.findings.length}.`,
    minWidthMm: MGN_DOOR_WIDTH_MM,
    totalChecked:
      classified.egressChecked + ramps.findings.length + maneuvering.findings.length,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    unmeasuredDoors: classified.unmeasured,
    ramps: ramps.findings,
    maneuvering: maneuvering.findings,
    unmeasuredManeuvering: maneuvering.unmeasured,
    source: MGN_DOOR_SOURCE,
    warnings,
  };
}

const exportRoomItemSchema = z.object({
  id: z.number(),
  uniqueId: z.string().optional().default(""),
  name: z.string().optional().default(""),
  number: z.string().optional().default(""),
  level: z.string().optional().default(""),
  area: z.number(),
  unboundedHeight: z.number(),
  /** Optional: exporter clear height (storey ΔZ − floor thickness), mm. */
  clearHeight: z.number().optional(),
  storeyHeight: z.number().optional(),
  floorThickness: z.number().optional(),
  heightSource: z.string().optional(),
  department: z.string().optional().default(""),
});

const tepLevelSchema = z.object({
  levelName: z.string(),
  elevation: z.number(),
  storeyKind: z.string(),
});

export interface RoomAreaRunnerResult {
  success: boolean;
  message: string;
  limits: RoomAreaLimit[];
  totalChecked: number;
  violations: ClassifiedRoomArea[];
  nearLimit: ClassifiedRoomArea[];
  compliant: ClassifiedRoomArea[];
  warnings: string[];
}

export interface RoomHeightRunnerResult {
  success: boolean;
  message: string;
  minHeightMm: number;
  source: NormAuditSource;
  totalChecked: number;
  violations: ClassifiedRoomHeight[];
  nearLimit: ClassifiedRoomHeight[];
  compliant: ClassifiedRoomHeight[];
  warnings: string[];
}

export interface StoreyHeightRunnerResult {
  success: boolean;
  message: string;
  minStoreyHeightMm: number;
  source: NormAuditSource;
  totalChecked: number;
  violations: ClassifiedStoreyHeight[];
  nearLimit: ClassifiedStoreyHeight[];
  compliant: ClassifiedStoreyHeight[];
  warnings: string[];
}

type ExportedRoomRow = RoomAreaInput & {
  unboundedHeightMm: number;
  exportedClearHeightMm?: number;
  exportedStoreyHeightMm?: number;
  floorThicknessMm?: number;
};

async function fetchExportRoomData(
  levelName: string,
  snapshot?: NormAuditRevitSnapshot
): Promise<ExportedRoomRow[]> {
  const rawResponse =
    snapshot?.exportRoomData ??
    (await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand("export_room_data", {
        includeUnplacedRooms: false,
        includeNotEnclosedRooms: false,
      });
    }));

  const raw = z
    .object({
      success: z.boolean(),
      message: z.string().optional().default(""),
      rooms: z.array(exportRoomItemSchema).optional().default([]),
    })
    .parse(rawResponse);

  if (!raw.success) {
    throw new Error(raw.message || "export_room_data failed.");
  }

  return raw.rooms
    .filter(
      (room) =>
        !levelName ||
        room.level.toLowerCase() === levelName.toLowerCase()
    )
    .map((room) => ({
      id: room.id,
      uniqueId: room.uniqueId || String(room.id),
      name: room.name,
      number: room.number,
      department: room.department,
      level: room.level,
      areaM2: room.area,
      unboundedHeightMm: room.unboundedHeight,
      exportedClearHeightMm: room.clearHeight,
      exportedStoreyHeightMm: room.storeyHeight,
      floorThicknessMm: room.floorThickness,
    }));
}

async function fetchTepLevels(
  snapshot?: NormAuditRevitSnapshot
): Promise<LevelElevation[]> {
  const rawResponse =
    snapshot?.exportTepData ??
    (await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand("export_tep_data", {});
    }));
  const raw = z
    .object({
      success: z.boolean(),
      message: z.string().optional().default(""),
      levels: z.array(tepLevelSchema).optional().default([]),
    })
    .parse(rawResponse);
  if (!raw.success) {
    throw new Error(raw.message || "export_tep_data failed.");
  }
  return raw.levels.map((level) => ({
    levelName: level.levelName,
    elevationMm: level.elevation,
    storeyKind: level.storeyKind,
  }));
}

/**
 * Room min-area check (REV-57). Uses export_room_data area (m²) vs per-category
 * limits resolved from the norm library. Room type from name/department keywords.
 */
export async function runRoomAreaCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  limits: RoomAreaLimit[];
  nearLimitToleranceM2?: number;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<RoomAreaRunnerResult> {
  if (options.limits.length === 0) {
    return {
      success: false,
      message:
        "Нет числовых норм площади помещений в библиотеке. Сделайте seed / query «площадь жилой комнаты».",
      limits: [],
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      warnings: [],
    };
  }

  const rooms = await fetchExportRoomData(options.levelName, options.snapshot);
  const classified = classifyRoomAreas(rooms, {
    limits: options.limits,
    nearLimitToleranceM2: options.nearLimitToleranceM2 ?? 0.5,
  });

  const warnings: string[] = [
    "v1: тип помещения определяется по имени/назначению (жилая, кухня, санузел, спальня). " +
      "Проверяются только категории с найденной нормой в библиотеке.",
  ];
  if (classified.skippedUnknown > 0) {
    warnings.push(
      `Помещений без распознанного типа (или исключённых): ${classified.skippedUnknown}.`
    );
  }
  if (classified.skippedNoLimit > 0) {
    warnings.push(
      `Помещений без применимой нормы площади: ${classified.skippedNoLimit}.`
    );
  }
  if (classified.missingArea > 0) {
    warnings.push(`Помещений без площади: ${classified.missingArea} (пропущены).`);
  }

  return {
    success: true,
    message: `Проверено помещений по площади: ${classified.checked}. Норм категорий: ${options.limits.length}.`,
    limits: options.limits,
    totalChecked: classified.checked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    warnings,
  };
}

/**
 * Room min-height check (REV-57).
 * Prefer clear height from level ΔZ − floor slab; do not trust Revit default
 * Room Limit Offset (often 8'-0" = 2438 mm).
 */
export async function runRoomHeightCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  minHeightMm: number;
  source: NormAuditSource;
  nearLimitToleranceMm?: number;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<RoomHeightRunnerResult> {
  if (!(options.minHeightMm > 0)) {
    return {
      success: false,
      message:
        "Нет числовой нормы высоты помещения. Сделайте seed / query «высота жилых помещений».",
      minHeightMm: 0,
      source: options.source,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      warnings: [],
    };
  }

  const [rooms, levels] = await Promise.all([
    fetchExportRoomData(options.levelName, options.snapshot),
    fetchTepLevels(options.snapshot).catch(() => [] as LevelElevation[]),
  ]);

  const resolvedRooms: RoomHeightInput[] = rooms.map((room) => {
    const resolved = resolveRoomClearHeight(
      {
        levelName: room.level,
        unboundedHeightMm: room.unboundedHeightMm,
        exportedClearHeightMm: room.exportedClearHeightMm,
        exportedStoreyHeightMm: room.exportedStoreyHeightMm,
        floorThicknessMm: room.floorThicknessMm,
      },
      levels
    );
    return {
      id: room.id,
      uniqueId: room.uniqueId,
      name: room.name,
      number: room.number,
      department: room.department,
      level: room.level,
      clearHeightMm: resolved.heightMm,
      heightSource: resolved.source,
    };
  });

  const classified = classifyRoomHeights(resolvedRooms, {
    minHeightMm: options.minHeightMm,
    nearLimitToleranceMm: options.nearLimitToleranceMm ?? 50,
  });

  const sourceCounts = resolvedRooms.reduce(
    (acc, room) => {
      const key = room.heightSource ?? "missing";
      acc[key] = (acc[key] ?? 0) + 1;
      return acc;
    },
    {} as Record<string, number>
  );

  const warnings: string[] = [
    "Высота помещений: ΔZ соседних уровней минус толщина перекрытия " +
      `(типично ${DEFAULT_FLOOR_THICKNESS_MM} мм, если толщина пола не пришла из export). ` +
      "Дефолт Room Limit Offset 8' (2438 мм) игнорируется.",
  ];
  if (levels.length === 0) {
    warnings.push(
      "export_tep_data недоступен — высота помещений без ΔZ уровней менее надёжна."
    );
  }
  if (sourceCounts.level_clear) {
    warnings.push(
      `Высота по уровням (ΔZ − перекрытие): ${sourceCounts.level_clear} пом.`
    );
  }
  if (classified.skipped > 0) {
    warnings.push(
      `Помещений вне жилых категорий (не проверялись): ${classified.skipped}.`
    );
  }
  if (classified.missingHeight > 0) {
    warnings.push(
      `Помещений без читаемой высоты: ${classified.missingHeight} (пропущены).`
    );
  }

  return {
    success: true,
    message: `Проверено помещений по высоте: ${classified.checked}. Норма ≥ ${options.minHeightMm} мм.`,
    minHeightMm: options.minHeightMm,
    source: options.source,
    totalChecked: classified.checked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    warnings,
  };
}

/**
 * Storey height check (REV-57). ΔZ between consecutive above-ground levels from export_tep_data.
 */
export async function runStoreyHeightCheck(options: {
  minStoreyHeightMm: number;
  source: NormAuditSource;
  nearLimitToleranceMm?: number;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<StoreyHeightRunnerResult> {
  if (!(options.minStoreyHeightMm > 0)) {
    return {
      success: false,
      message:
        "Нет числовой нормы высоты этажа. Сделайте seed / query «высота этажа жилого здания».",
      minStoreyHeightMm: 0,
      source: options.source,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      warnings: [],
    };
  }

  const rawResponse =
    options.snapshot?.exportTepData ??
    (await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand("export_tep_data", {});
    }));

  const raw = z
    .object({
      success: z.boolean(),
      message: z.string().optional().default(""),
      levels: z.array(tepLevelSchema).optional().default([]),
    })
    .parse(rawResponse);

  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "export_tep_data failed.",
      minStoreyHeightMm: options.minStoreyHeightMm,
      source: options.source,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      warnings: [],
    };
  }

  const levels: LevelInput[] = raw.levels.map((level) => ({
    levelName: level.levelName,
    elevationMm: level.elevation,
    storeyKind: level.storeyKind,
  }));

  const classified = classifyStoreyHeights(levels, {
    minStoreyHeightMm: options.minStoreyHeightMm,
    nearLimitToleranceMm: options.nearLimitToleranceMm ?? 50,
  });

  const warnings: string[] = [
    "v1: высота этажа = разница отметок соседних надземных уровней (export_tep_data). " +
      "Технические / подвальные уровни не участвуют.",
  ];
  if (classified.checked === 0) {
    warnings.push(
      "Недостаточно надземных уровней для расчёта высоты этажа (нужно ≥ 2)."
    );
  }

  return {
    success: true,
    message: `Проверено перепадов этажей: ${classified.checked}. Норма ≥ ${options.minStoreyHeightMm} мм.`,
    minStoreyHeightMm: options.minStoreyHeightMm,
    source: options.source,
    totalChecked: classified.checked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    warnings,
  };
}

export interface HighlightAuditResult {
  highlightedCount: number;
  filledRegionCount: number;
  doorCount: number;
  otherElementCount: number;
  message: string;
}

export function paintTargetsFromFindings(findings: NormAuditFinding[]): {
  roomIds: number[];
  doorIds: number[];
  otherElementIds: number[];
} {
  const roomSeen = new Set<number>();
  const doorSeen = new Set<number>();
  const roomIds: number[] = [];
  const doorIds: number[] = [];
  const otherElementIds: number[] = [];
  const otherSeen = new Set<number>();

  for (const finding of findings) {
    if (finding.status !== "violation" && finding.status !== "nearLimit") {
      continue;
    }
    if (
      finding.checkType === "fire_doors" ||
      finding.checkType === "door_clear_width" ||
      finding.checkType === "mgn_door_width" ||
      finding.checkType === "mgn_door_maneuvering"
    ) {
      if (!doorSeen.has(finding.elementId)) {
        doorSeen.add(finding.elementId);
        doorIds.push(finding.elementId);
      }
      continue;
    }
    if (finding.checkType === "mgn_ramp_slope") {
      if (!otherSeen.has(finding.elementId)) {
        otherSeen.add(finding.elementId);
        otherElementIds.push(finding.elementId);
      }
      continue;
    }
    if (finding.checkType === "storey_height") {
      continue;
    }
    if (!roomSeen.has(finding.elementId)) {
      roomSeen.add(finding.elementId);
      roomIds.push(finding.elementId);
    }
  }

  return { roomIds, doorIds, otherElementIds };
}

const openingGeometryItemSchema = z.object({
  id: z.number(),
  uniqueId: z.string().optional().default(""),
  category: z.string().optional().default(""),
  family: z.string().optional().default(""),
  type: z.string().optional().default(""),
  level: z.string().optional().default(""),
  sillHeightMm: z.number().nullable().optional(),
  openingHeightMm: z.number().nullable().optional(),
  isOnEgressPath: z.boolean().optional().default(false),
});

export interface WindowSillRunnerResult {
  success: boolean;
  message: string;
  minSillHeightMm: number;
  totalChecked: number;
  violations: ClassifiedWindowSill[];
  nearLimit: ClassifiedWindowSill[];
  compliant: ClassifiedWindowSill[];
  source: NormAuditSource;
  warnings: string[];
}

export interface OpeningHeightRunnerResult {
  success: boolean;
  message: string;
  minHeightMm: number;
  totalChecked: number;
  violations: ClassifiedOpeningHeight[];
  nearLimit: ClassifiedOpeningHeight[];
  compliant: ClassifiedOpeningHeight[];
  source: NormAuditSource;
  warnings: string[];
}

async function fetchOpeningGeometry(
  levelName: string,
  snapshot?: NormAuditRevitSnapshot
) {
  const rawResponse =
    snapshot?.openingGeometryInfo ??
    (await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand("get_opening_geometry_info", {
        levelName,
      });
    }));

  return z
    .object({
      success: z.boolean(),
      message: z.string().optional().default(""),
      totalOpenings: z.number().optional(),
      openings: z.array(openingGeometryItemSchema).optional().default([]),
    })
    .parse(rawResponse);
}

/**
 * Window sill height check (REV-58).
 * Reads sillHeightMm via get_opening_geometry_info and compares against min.
 */
export async function runWindowSillCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  minSillHeightMm: number;
  source: NormAuditSource;
  nearLimitToleranceMm?: number;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<WindowSillRunnerResult> {
  if (!(options.minSillHeightMm > 0)) {
    return {
      success: false,
      message:
        "Нет числовой нормы высоты подоконника. Сделайте seed / query «высота подоконника».",
      minSillHeightMm: 0,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }

  const raw = await fetchOpeningGeometry(options.levelName, options.snapshot);
  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "get_opening_geometry_info failed.",
      minSillHeightMm: options.minSillHeightMm,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }

  const openings: WindowSillInput[] = raw.openings.map((item) => ({
    id: item.id,
    uniqueId: item.uniqueId || String(item.id),
    family: item.family,
    type: item.type,
    level: item.level,
    category: item.category || "window",
    sillHeightMm: item.sillHeightMm ?? null,
  }));

  const classified = classifyWindowSills(openings, {
    minSillHeightMm: options.minSillHeightMm,
    nearLimitToleranceMm: options.nearLimitToleranceMm ?? 50,
  });

  const warnings: string[] = [];
  if (classified.accessoriesSkipped > 0) {
    warnings.push(
      `Аксессуары окон (откос/подоконник-заполнение) исключены: ${classified.accessoriesSkipped}.`
    );
  }
  if (classified.missingSill > 0) {
    warnings.push(
      `Окон без читаемой высоты подоконника: ${classified.missingSill} (пропущены).`
    );
  }

  return {
    success: true,
    message:
      `Проверено окон: ${classified.checked} (из ${classified.totalWindows}). ` +
      `Норма подоконника ≥ ${options.minSillHeightMm} мм.`,
    minSillHeightMm: options.minSillHeightMm,
    totalChecked: classified.checked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    source: options.source,
    warnings,
  };
}

/**
 * Opening height check (REV-58).
 * v1: nominal DOOR_HEIGHT for egress doors vs «высота эвак. выходов в свету».
 */
export async function runOpeningHeightCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  minHeightMm: number;
  source: NormAuditSource;
  nearLimitToleranceMm?: number;
  egressDoorsOnly?: boolean;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<OpeningHeightRunnerResult> {
  if (!(options.minHeightMm > 0)) {
    return {
      success: false,
      message:
        "Нет числовой нормы высоты проёма. Сделайте seed / query «высота эвакуационных выходов».",
      minHeightMm: 0,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }

  const raw = await fetchOpeningGeometry(options.levelName, options.snapshot);
  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "get_opening_geometry_info failed.",
      minHeightMm: options.minHeightMm,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }

  const openings: OpeningHeightInput[] = raw.openings.map((item) => ({
    id: item.id,
    uniqueId: item.uniqueId || String(item.id),
    family: item.family,
    type: item.type,
    level: item.level,
    category: item.category || "door",
    openingHeightMm: item.openingHeightMm ?? null,
    isOnEgressPath: item.isOnEgressPath,
  }));

  const classified = classifyOpeningHeights(openings, {
    minHeightMm: options.minHeightMm,
    nearLimitToleranceMm: options.nearLimitToleranceMm ?? 50,
    egressDoorsOnly: options.egressDoorsOnly ?? true,
  });

  const warnings: string[] = [
    "v1: сравнивается номинальная высота параметра (DOOR_HEIGHT / WINDOW_HEIGHT). " +
      "По умолчанию — двери на путях эвакуации (норма «высота выходов в свету»).",
  ];
  if (classified.accessoriesSkipped > 0) {
    warnings.push(
      `Откосы/аксессуары исключены: ${classified.accessoriesSkipped}.`
    );
  }
  if (classified.nonEgressSkipped > 0) {
    warnings.push(
      `Двери вне путей эвакуации не проверялись: ${classified.nonEgressSkipped}.`
    );
  }
  if (classified.windowsSkipped > 0) {
    warnings.push(
      `Окна не сравнивались с нормой эвак. выхода: ${classified.windowsSkipped}.`
    );
  }
  if (classified.missingHeight > 0) {
    warnings.push(
      `Проёмов без читаемой высоты: ${classified.missingHeight} (пропущены).`
    );
  }

  return {
    success: true,
    message:
      `Проверено проёмов: ${classified.checked} (блоков после фильтра: ${classified.totalOpenings}). ` +
      `Норма высоты ≥ ${options.minHeightMm} мм.`,
    minHeightMm: options.minHeightMm,
    totalChecked: classified.checked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    source: options.source,
    warnings,
  };
}

const verticalCirculationSchema = z.object({
  success: z.boolean(),
  message: z.string().optional().default(""),
  stairs: z
    .array(
      z.object({
        id: z.number(),
        uniqueId: z.string().optional().default(""),
        name: z.string().optional().default(""),
        type: z.string().optional().default(""),
        level: z.string().optional().default(""),
        widthMm: z.number().nullable().optional(),
        riserMm: z.number().nullable().optional(),
        treadMm: z.number().nullable().optional(),
      })
    )
    .optional()
    .default([]),
  ramps: z
    .array(
      z.object({
        id: z.number(),
        uniqueId: z.string().optional().default(""),
        name: z.string().optional().default(""),
        type: z.string().optional().default(""),
        level: z.string().optional().default(""),
        widthMm: z.number().nullable().optional(),
        slopePercent: z.number().nullable().optional(),
      })
    )
    .optional()
    .default([]),
  railings: z
    .array(
      z.object({
        id: z.number(),
        uniqueId: z.string().optional().default(""),
        name: z.string().optional().default(""),
        type: z.string().optional().default(""),
        level: z.string().optional().default(""),
        heightMm: z.number().nullable().optional(),
      })
    )
    .optional()
    .default([]),
});

async function fetchVerticalCirculation(
  levelName: string,
  snapshot?: NormAuditRevitSnapshot
) {
  const rawResponse =
    snapshot?.verticalCirculationInfo ??
    (await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand("get_vertical_circulation_info", {
        levelName,
      });
    }));
  return verticalCirculationSchema.parse(rawResponse);
}

export interface StairWidthRunnerResult {
  success: boolean;
  message: string;
  minWidthMm: number;
  totalChecked: number;
  violations: ClassifiedStairWidth[];
  nearLimit: ClassifiedStairWidth[];
  compliant: ClassifiedStairWidth[];
  source: NormAuditSource;
  warnings: string[];
}

export async function runStairWidthCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  minWidthMm: number;
  source: NormAuditSource;
  nearLimitToleranceMm?: number;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<StairWidthRunnerResult> {
  if (!(options.minWidthMm > 0)) {
    return {
      success: false,
      message: "Нет нормы ширины марша.",
      minWidthMm: 0,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }
  const raw = await fetchVerticalCirculation(options.levelName, options.snapshot);
  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "get_vertical_circulation_info failed.",
      minWidthMm: options.minWidthMm,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }
  const classified = classifyStairWidths(raw.stairs, {
    minWidthMm: options.minWidthMm,
    nearLimitToleranceMm: options.nearLimitToleranceMm ?? 50,
  });
  const warnings: string[] = [];
  if (classified.missingWidth > 0) {
    warnings.push(`Лестниц без ширины марша: ${classified.missingWidth}.`);
  }
  return {
    success: true,
    message: `Проверено лестниц: ${classified.checked}. Норма ширины марша ≥ ${options.minWidthMm} мм.`,
    minWidthMm: options.minWidthMm,
    totalChecked: classified.checked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    source: options.source,
    warnings,
  };
}

export interface StairRiserTreadRunnerResult {
  success: boolean;
  message: string;
  maxRiserMm?: number;
  minTreadMm?: number;
  totalChecked: number;
  violations: ClassifiedStairRiserTread[];
  nearLimit: ClassifiedStairRiserTread[];
  compliant: ClassifiedStairRiserTread[];
  source: NormAuditSource;
  warnings: string[];
}

export async function runStairRiserTreadCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  maxRiserMm?: number | null;
  minTreadMm?: number | null;
  source: NormAuditSource;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<StairRiserTreadRunnerResult> {
  const raw = await fetchVerticalCirculation(options.levelName, options.snapshot);
  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "get_vertical_circulation_info failed.",
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }
  const classified = classifyStairRiserTreads(raw.stairs, {
    maxRiserMm: options.maxRiserMm,
    minTreadMm: options.minTreadMm,
  });
  return {
    success: true,
    message: `Проверено ступеней (подступенок/проступь): ${classified.checked}.`,
    maxRiserMm: options.maxRiserMm ?? undefined,
    minTreadMm: options.minTreadMm ?? undefined,
    totalChecked: classified.checked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    source: options.source,
    warnings:
      classified.missing > 0
        ? [`Нет riser/tread у ${classified.missing} измерений.`]
        : [],
  };
}

export interface RampRunnerResult {
  success: boolean;
  message: string;
  minWidthMm?: number;
  maxSlopePercent?: number;
  totalChecked: number;
  violations: ClassifiedRamp[];
  nearLimit: ClassifiedRamp[];
  compliant: ClassifiedRamp[];
  source: NormAuditSource;
  warnings: string[];
}

export async function runRampCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  minWidthMm?: number | null;
  maxSlopePercent?: number | null;
  source: NormAuditSource;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<RampRunnerResult> {
  const raw = await fetchVerticalCirculation(options.levelName, options.snapshot);
  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "get_vertical_circulation_info failed.",
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }
  const classified = classifyRamps(raw.ramps, {
    minWidthMm: options.minWidthMm,
    maxSlopePercent: options.maxSlopePercent,
  });
  return {
    success: true,
    message: `Проверено пандусов: ${classified.checked}.`,
    minWidthMm: options.minWidthMm ?? undefined,
    maxSlopePercent: options.maxSlopePercent ?? undefined,
    totalChecked: classified.checked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    source: options.source,
    warnings:
      classified.missing > 0
        ? [`Нет ширины/уклона у ${classified.missing} измерений.`]
        : [],
  };
}

export interface RailingHeightRunnerResult {
  success: boolean;
  message: string;
  minHeightMm: number;
  totalChecked: number;
  violations: ClassifiedRailing[];
  nearLimit: ClassifiedRailing[];
  compliant: ClassifiedRailing[];
  source: NormAuditSource;
  warnings: string[];
}

export async function runRailingHeightCheck(options: {
  levelName: string;
  includeCompliant: boolean;
  minHeightMm: number;
  source: NormAuditSource;
  snapshot?: NormAuditRevitSnapshot;
}): Promise<RailingHeightRunnerResult> {
  if (!(options.minHeightMm > 0)) {
    return {
      success: false,
      message: "Нет нормы высоты ограждения.",
      minHeightMm: 0,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }
  const raw = await fetchVerticalCirculation(options.levelName, options.snapshot);
  if (!raw.success) {
    return {
      success: false,
      message: raw.message || "get_vertical_circulation_info failed.",
      minHeightMm: options.minHeightMm,
      totalChecked: 0,
      violations: [],
      nearLimit: [],
      compliant: [],
      source: options.source,
      warnings: [],
    };
  }
  const classified = classifyRailingHeights(raw.railings, {
    minHeightMm: options.minHeightMm,
  });
  const warnings: string[] = [];
  if (classified.missingHeight > 0) {
    warnings.push(`Ограждений без высоты: ${classified.missingHeight}.`);
  }
  if (classified.skippedHandrails > 0) {
    warnings.push(
      `Пропущено поручней МГН (норма 0,8–0,9 м, не балконное ограждение): ${classified.skippedHandrails}.`
    );
  }
  if (classified.skippedAccessories > 0) {
    warnings.push(
      `Пропущено аксессуаров ограждения: ${classified.skippedAccessories}.`
    );
  }
  return {
    success: true,
    message: `Проверено ограждений: ${classified.checked}. Норма ≥ ${options.minHeightMm} мм.`,
    minHeightMm: options.minHeightMm,
    totalChecked: classified.checked,
    violations: classified.violations,
    nearLimit: classified.nearLimit,
    compliant: classified.compliant,
    source: options.source,
    warnings,
  };
}

/**
 * Paint audit violations on the active floor plan:
 * rooms → Annotate Filled Region; fire doors → Override SetColor red.
 */
export async function highlightAuditViolations(options: {
  findings: NormAuditFinding[];
}): Promise<HighlightAuditResult> {
  const { roomIds, doorIds, otherElementIds } = paintTargetsFromFindings(options.findings);

  if (roomIds.length === 0 && doorIds.length === 0 && otherElementIds.length === 0) {
    return {
      highlightedCount: 0,
      filledRegionCount: 0,
      doorCount: 0,
      otherElementCount: 0,
      message: "Нет нарушений для заливки.",
    };
  }

  let filledRegionCount = 0;
  let doorCount = 0;
  let otherElementCount = 0;

  await withRevitConnection(async (revitClient) => {
    if (roomIds.length > 0) {
      const response = await revitClient.sendCommand("create_filled_regions", {
        roomIds,
        colorPreset: "red",
        clearPrevious: true,
        filledRegionTypeName: "ADSK_У_Сплошная_Красный",
        commentTag: "MCP-FR",
      });
      if (
        typeof response === "object" &&
        response !== null &&
        "createdCount" in response
      ) {
        filledRegionCount = Number(
          (response as { createdCount?: number }).createdCount
        );
      }
    }

    if (doorIds.length > 0) {
      await revitClient.sendCommand("operate_element", {
        data: {
          elementIds: doorIds,
          action: "SetColor",
          colorValue: [255, 0, 0],
        },
      });
      doorCount = doorIds.length;
    }
    if (otherElementIds.length > 0) {
      await revitClient.sendCommand("operate_element", {
        data: {
          elementIds: otherElementIds,
          action: "SetColor",
          colorValue: [255, 0, 0],
        },
      });
      otherElementCount = otherElementIds.length;
    }
  });

  const highlightedCount = filledRegionCount + doorCount + otherElementCount;
  const parts: string[] = [];
  if (filledRegionCount > 0) {
    parts.push(`цветовых областей: ${filledRegionCount}`);
  }
  if (doorCount > 0) {
    parts.push(`дверей: ${doorCount}`);
  }
  if (otherElementCount > 0) {
    parts.push(`прочих элементов: ${otherElementCount}`);
  }

  return {
    highlightedCount,
    filledRegionCount,
    doorCount,
    otherElementCount,
    message:
      parts.length > 0
        ? `Заливка нарушений — ${parts.join(", ")}.`
        : "Заливка не создана (проверьте активный план этажа).",
  };
}

export async function resolveLevelNameFromView(): Promise<string> {
  try {
    const response = await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand("get_current_view_info", {});
    });
    if (typeof response === "object" && response !== null) {
      const level =
        (response as { levelName?: string; LevelName?: string }).levelName ??
        (response as { levelName?: string; LevelName?: string }).LevelName ??
        (response as { view?: { levelName?: string } }).view?.levelName;
      if (typeof level === "string" && level.trim()) return level.trim();

      const name =
        (response as { name?: string }).name ??
        (response as { viewName?: string }).viewName;
      // Floor plan names often contain the level, e.g. «01 – План 1 этажа»
      if (typeof name === "string" && /этаж/i.test(name)) {
        const match = name.match(/(\d+\s*этаж|[^\s]+\s*этаж)/i);
        if (match) return match[1];
      }
    }
  } catch {
    // ignore — audit can run without level filter
  }
  return "";
}
