import { z } from "zod";
import { withRevitConnection } from "../../utils/ConnectionManager.js";
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
}): Promise<MinDimensionsRunnerResult> {
  const { rules, warnings } = await loadMinDimensionRulesFromNormatives({});
  const limits = resolveMinDimensionLimits(rules, {});
  const hasLimit =
    limits.minBalconyWidthMm !== undefined ||
    limits.minLoggiaWidthMm !== undefined ||
    limits.minLoggiaDepthMm !== undefined ||
    limits.minFirePierToOpeningMm !== undefined ||
    limits.minFirePierBetweenOpeningsMm !== undefined;

  if (!hasLimit) {
    return {
      success: false,
      message: "Не удалось определить лимиты лоджий/балконов/простенков из normatives/.",
      totalChecked: 0,
      violations: [],
      compliant: [],
      appliedRules: [],
      fallbackSource: toAuditSource(null),
      warnings: warnings ?? [],
    };
  }

  const rawResponse = await withRevitConnection(async (revitClient) => {
    return await revitClient.sendCommand("check_min_dimensions", {
      minBalconyWidthMm: limits.minBalconyWidthMm,
      minLoggiaWidthMm: limits.minLoggiaWidthMm,
      minLoggiaDepthMm: limits.minLoggiaDepthMm,
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
      warnings: warnings ?? [],
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
      ...(warnings ?? []),
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
  isOnEgressPath: z.boolean().optional().default(false),
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
  source: NormAuditSource;
  warnings: string[];
}

/**
 * Door clear-width check (REV-56, v1 = nominal DOOR_WIDTH).
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
      source: options.source,
      warnings: [],
    };
  }

  const rawResponse = await withRevitConnection(async (revitClient) => {
    return await revitClient.sendCommand("get_door_egress_info", {
      levelName: options.levelName,
    });
  });

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
    isOnEgressPath: door.isOnEgressPath,
  }));

  const classified = classifyDoorWidths(doors, {
    minWidthMm: options.minWidthMm,
    nearLimitToleranceMm: options.nearLimitToleranceMm ?? 50,
    egressOnly: options.egressOnly ?? true,
  });

  const warnings: string[] = [
    "v1: сравнивается номинальная ширина параметра двери (DOOR_WIDTH). " +
      "Ширина «в свету» (за вычетом коробки) — отдельный follow-up.",
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
    source: options.source,
    warnings,
  };
}

export interface HighlightAuditResult {
  highlightedCount: number;
  filledRegionCount: number;
  doorCount: number;
  message: string;
}

function paintTargetsFromFindings(findings: NormAuditFinding[]): {
  roomIds: number[];
  doorIds: number[];
} {
  const roomSeen = new Set<number>();
  const doorSeen = new Set<number>();
  const roomIds: number[] = [];
  const doorIds: number[] = [];

  for (const finding of findings) {
    if (finding.status !== "violation" && finding.status !== "nearLimit") {
      continue;
    }
    if (
      finding.checkType === "fire_doors" ||
      finding.checkType === "door_clear_width"
    ) {
      if (!doorSeen.has(finding.elementId)) {
        doorSeen.add(finding.elementId);
        doorIds.push(finding.elementId);
      }
      continue;
    }
    if (!roomSeen.has(finding.elementId)) {
      roomSeen.add(finding.elementId);
      roomIds.push(finding.elementId);
    }
  }

  return { roomIds, doorIds };
}

/**
 * Paint audit violations on the active floor plan:
 * rooms → Annotate Filled Region; fire doors → Override SetColor red.
 */
export async function highlightAuditViolations(options: {
  findings: NormAuditFinding[];
}): Promise<HighlightAuditResult> {
  const { roomIds, doorIds } = paintTargetsFromFindings(options.findings);

  if (roomIds.length === 0 && doorIds.length === 0) {
    return {
      highlightedCount: 0,
      filledRegionCount: 0,
      doorCount: 0,
      message: "Нет нарушений для заливки.",
    };
  }

  let filledRegionCount = 0;
  let doorCount = 0;

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
  });

  const highlightedCount = filledRegionCount + doorCount;
  const parts: string[] = [];
  if (filledRegionCount > 0) {
    parts.push(`цветовых областей: ${filledRegionCount}`);
  }
  if (doorCount > 0) {
    parts.push(`дверей ПД: ${doorCount}`);
  }

  return {
    highlightedCount,
    filledRegionCount,
    doorCount,
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
