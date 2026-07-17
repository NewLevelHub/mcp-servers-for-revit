import type { Database } from "better-sqlite3";
import {
  AUDIT_SCOPE_NOTE,
  selectPhase1Checkers,
  selectSkippedRules,
  type NormAuditCheckerDef,
} from "./checklist.js";
import { formatNormAuditReport } from "./formatAuditReport.js";
import {
  normalizeDoorWidthFindings,
  normalizeEvacuationFindings,
  normalizeFireDoorFindings,
  normalizeMinDimensionFindings,
  normalizeRoomDepthFindings,
  normalizeRoomAreaFindings,
  normalizeRoomHeightFindings,
  normalizeStoreyHeightFindings,
  normalizeTambourSizeFindings,
  normalizeWindowSillFindings,
  normalizeOpeningHeightFindings,
  normalizeStairWidthFindings,
  normalizeStairRiserTreadFindings,
  normalizeRampFindings,
  normalizeRailingHeightFindings,
  summarizeFindings,
} from "./normalizeFindings.js";
import { resolveRoomDepthLimitFromLibrary } from "./resolveDepthLimit.js";
import { resolveDoorWidthLimitFromLibrary } from "./resolveDoorWidth.js";
import { resolveRoomAreaLimitsFromLibrary } from "./resolveRoomAreaLimits.js";
import { resolveRoomHeightLimitFromLibrary } from "./resolveRoomHeightLimit.js";
import { resolveStoreyHeightLimitFromLibrary } from "./resolveStoreyHeightLimit.js";
import { resolveTambourSizeLimitFromLibrary } from "./resolveTambourSize.js";
import { resolveWindowSillLimitFromLibrary } from "./resolveWindowSill.js";
import { resolveOpeningHeightLimitFromLibrary } from "./resolveOpeningHeight.js";
import {
  resolveStairWidthLimitFromLibrary,
  resolveStairRiserTreadLimitsFromLibrary,
  resolveRampLimitsFromLibrary,
  resolveRailingHeightLimitFromLibrary,
} from "./resolveVerticalCirculation.js";
import {
  highlightAuditViolations,
  resolveLevelNameFromView,
  runDoorWidthCheck,
  runEvacuationWidthCheck,
  runFireDoorsCheck,
  runMinDimensionsCheck,
  runOpeningHeightCheck,
  runRailingHeightCheck,
  runRampCheck,
  runRoomAreaCheck,
  runRoomDepthCheck,
  runRoomHeightCheck,
  runStairRiserTreadCheck,
  runStairWidthCheck,
  runStoreyHeightCheck,
  runTambourSizeCheck,
  runWindowSillCheck,
  type DoorWidthRunnerResult,
  type EvacuationWidthRunnerResult,
  type FireDoorsRunnerResult,
  type MinDimensionsRunnerResult,
  type OpeningHeightRunnerResult,
  type RailingHeightRunnerResult,
  type RampRunnerResult,
  type RoomAreaRunnerResult,
  type RoomDepthRunnerResult,
  type RoomHeightRunnerResult,
  type StairRiserTreadRunnerResult,
  type StairWidthRunnerResult,
  type StoreyHeightRunnerResult,
  type TambourSizeRunnerResult,
  type WindowSillRunnerResult,
} from "./runners.js";
import type {
  NormAuditCheckRunSummary,
  NormAuditFinding,
  NormAuditResult,
  NormAuditSource,
} from "./types.js";
import { toAuditSource } from "./types.js";

export interface NormAuditDeps {
  db?: Database;
  resolveLevelName?: () => Promise<string>;
  runEvacuation?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    nearLimitToleranceMm?: number;
  }) => Promise<EvacuationWidthRunnerResult>;
  runRoomDepth?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    minDepthMm?: number;
    maxDepthMm?: number;
    source: NormAuditSource;
  }) => Promise<RoomDepthRunnerResult>;
  runMinDimensions?: (opts: {
    levelName: string;
    includeCompliant: boolean;
  }) => Promise<MinDimensionsRunnerResult>;
  runFireDoors?: (opts: {
    levelName: string;
  }) => Promise<FireDoorsRunnerResult>;
  runDoorWidth?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    minWidthMm: number;
    source: NormAuditSource;
    nearLimitToleranceMm?: number;
  }) => Promise<DoorWidthRunnerResult>;
  runTambourSize?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    minSideMm: number;
    source: NormAuditSource;
    nearLimitToleranceMm?: number;
  }) => Promise<TambourSizeRunnerResult>;
  highlight?: (opts: {
    findings: NormAuditFinding[];
  }) => Promise<{
    highlightedCount: number;
    filledRegionCount?: number;
    doorCount?: number;
    message: string;
  }>;
  resolveDepthLimit?: (
    db: Database
  ) => ReturnType<typeof resolveRoomDepthLimitFromLibrary>;
  resolveDoorWidth?: (
    db: Database
  ) => ReturnType<typeof resolveDoorWidthLimitFromLibrary>;
  resolveTambourSize?: (
    db: Database
  ) => ReturnType<typeof resolveTambourSizeLimitFromLibrary>;
  runRoomArea?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    limits: ReturnType<typeof resolveRoomAreaLimitsFromLibrary>;
    nearLimitToleranceM2?: number;
  }) => Promise<RoomAreaRunnerResult>;
  runRoomHeight?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    minHeightMm: number;
    source: NormAuditSource;
    nearLimitToleranceMm?: number;
  }) => Promise<RoomHeightRunnerResult>;
  runStoreyHeight?: (opts: {
    minStoreyHeightMm: number;
    source: NormAuditSource;
    nearLimitToleranceMm?: number;
  }) => Promise<StoreyHeightRunnerResult>;
  resolveRoomAreaLimits?: (
    db: Database
  ) => ReturnType<typeof resolveRoomAreaLimitsFromLibrary>;
  resolveRoomHeight?: (
    db: Database
  ) => ReturnType<typeof resolveRoomHeightLimitFromLibrary>;
  resolveStoreyHeight?: (
    db: Database
  ) => ReturnType<typeof resolveStoreyHeightLimitFromLibrary>;
  runWindowSill?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    minSillHeightMm: number;
    source: NormAuditSource;
    nearLimitToleranceMm?: number;
  }) => Promise<WindowSillRunnerResult>;
  runOpeningHeight?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    minHeightMm: number;
    source: NormAuditSource;
    nearLimitToleranceMm?: number;
  }) => Promise<OpeningHeightRunnerResult>;
  resolveWindowSill?: (
    db: Database
  ) => ReturnType<typeof resolveWindowSillLimitFromLibrary>;
  resolveOpeningHeight?: (
    db: Database
  ) => ReturnType<typeof resolveOpeningHeightLimitFromLibrary>;
  runStairWidth?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    minWidthMm: number;
    source: NormAuditSource;
    nearLimitToleranceMm?: number;
  }) => Promise<StairWidthRunnerResult>;
  runStairRiserTread?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    maxRiserMm?: number | null;
    minTreadMm?: number | null;
    source: NormAuditSource;
  }) => Promise<StairRiserTreadRunnerResult>;
  runRamp?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    minWidthMm?: number | null;
    maxSlopePercent?: number | null;
    source: NormAuditSource;
  }) => Promise<RampRunnerResult>;
  runRailingHeight?: (opts: {
    levelName: string;
    includeCompliant: boolean;
    minHeightMm: number;
    source: NormAuditSource;
  }) => Promise<RailingHeightRunnerResult>;
  resolveStairWidth?: (
    db: Database
  ) => ReturnType<typeof resolveStairWidthLimitFromLibrary>;
  resolveStairRiserTread?: (
    db: Database
  ) => ReturnType<typeof resolveStairRiserTreadLimitsFromLibrary>;
  resolveRamp?: (
    db: Database
  ) => ReturnType<typeof resolveRampLimitsFromLibrary>;
  resolveRailingHeight?: (
    db: Database
  ) => ReturnType<typeof resolveRailingHeightLimitFromLibrary>;
}

export interface RunNormAuditOptions {
  levelName?: string;
  scope?: "floor" | "project";
  topics?: string[];
  mode?: "report" | "highlight";
  includeCompliant?: boolean;
  nearLimitToleranceMm?: number;
}

function sourceForMinDimItem(
  item: { metric?: string; spaceKind?: string },
  appliedRules: MinDimensionsRunnerResult["appliedRules"]
): NormAuditSource | undefined {
  const metric = item.metric ?? "";
  const kind = (item.spaceKind ?? "").toLowerCase();
  const match = appliedRules.find((rule) => {
    if (rule.metric !== metric && metric) {
      // pier metrics map 1:1; width/depth need object match
      if (metric === "width" || metric === "depth") {
        const obj = rule.object.toLowerCase();
        if (kind.includes("балкон") && obj.includes("балкон")) return rule.metric === metric;
        if (kind.includes("лоджи") && obj.includes("лоджи")) return rule.metric === metric;
        return false;
      }
      return false;
    }
    return rule.metric === metric || (!metric && true);
  });
  return match ? toAuditSource(match.source) : undefined;
}

async function runOneChecker(
  checker: NormAuditCheckerDef,
  ctx: {
    levelName: string;
    includeCompliant: boolean;
    nearLimitToleranceMm: number;
    deps: Required<
      Pick<
        NormAuditDeps,
        | "runEvacuation"
        | "runRoomDepth"
        | "runMinDimensions"
        | "runFireDoors"
        | "runDoorWidth"
        | "runTambourSize"
        | "runRoomArea"
        | "runRoomHeight"
        | "runStoreyHeight"
        | "runWindowSill"
        | "runOpeningHeight"
        | "runStairWidth"
        | "runStairRiserTread"
        | "runRamp"
        | "runRailingHeight"
        | "resolveDepthLimit"
        | "resolveDoorWidth"
        | "resolveTambourSize"
        | "resolveRoomAreaLimits"
        | "resolveRoomHeight"
        | "resolveStoreyHeight"
        | "resolveWindowSill"
        | "resolveOpeningHeight"
        | "resolveStairWidth"
        | "resolveStairRiserTread"
        | "resolveRamp"
        | "resolveRailingHeight"
      >
    > & { db?: Database };
  }
): Promise<{
  findings: NormAuditFinding[];
  check: NormAuditCheckRunSummary;
  warnings: string[];
}> {
  const warnings: string[] = [];

  try {
    switch (checker.checkType) {
      case "evacuation_width": {
        const result = await ctx.deps.runEvacuation({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          nearLimitToleranceMm: ctx.nearLimitToleranceMm,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        return {
          findings: normalizeEvacuationFindings({
            violations: result.violations,
            nearLimit: result.nearLimit,
            compliant: result.compliant,
            source: result.source,
            minWidthMm: result.minWidthMm,
          }),
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "room_depth": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — глубину пропускаем.",
            },
            warnings: ["room_depth: нет подключения к библиотеке норм"],
          };
        }
        const limit = ctx.deps.resolveDepthLimit(ctx.deps.db);
        if (!limit) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет числового правила глубины помещения — сделайте seed.",
            },
            warnings: [
              "room_depth: правило глубины не найдено в библиотеке (skipped, не violation)",
            ],
          };
        }
        const result = await ctx.deps.runRoomDepth({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          minDepthMm: limit.minDepthMm,
          maxDepthMm: limit.maxDepthMm,
          source: limit.source,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        return {
          findings: normalizeRoomDepthFindings({
            violations: result.violations,
            compliant: result.compliant,
            source: result.source,
            minDepthMm: result.minDepthMm,
            maxDepthMm: result.maxDepthMm,
          }),
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "min_dimensions": {
        const result = await ctx.deps.runMinDimensions({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        return {
          findings: normalizeMinDimensionFindings({
            violations: result.violations,
            compliant: result.compliant,
            sourceForItem: (item) =>
              sourceForMinDimItem(item, result.appliedRules),
            fallbackSource: result.fallbackSource,
          }),
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "fire_doors": {
        const result = await ctx.deps.runFireDoors({
          levelName: ctx.levelName,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeFireDoorFindings(result.doors);
        // When includeCompliant=false, drop compliant fire-door findings
        const filtered = ctx.includeCompliant
          ? findings
          : findings.filter((f) => f.status !== "compliant");
        return {
          findings: filtered,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "door_clear_width": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — ширину дверей пропускаем.",
            },
            warnings: ["door_clear_width: нет подключения к библиотеке норм"],
          };
        }
        const limit = ctx.deps.resolveDoorWidth(ctx.deps.db);
        if (!limit) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет числовой нормы ширины двери/проёма — сделайте seed.",
            },
            warnings: [
              "door_clear_width: правило ширины двери не найдено в библиотеке (skipped, не violation)",
            ],
          };
        }
        const result = await ctx.deps.runDoorWidth({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          minWidthMm: limit.minWidthMm,
          source: limit.source,
          nearLimitToleranceMm: 50,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeDoorWidthFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
          source: result.source,
          minWidthMm: result.minWidthMm,
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "tambour_size_min": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — габарит тамбура пропускаем.",
            },
            warnings: ["tambour_size_min: нет подключения к библиотеке норм"],
          };
        }
        const limit = ctx.deps.resolveTambourSize!(ctx.deps.db);
        if (!limit) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет числовой нормы габарита тамбура — сделайте seed.",
            },
            warnings: [
              "tambour_size_min: правило габарита тамбура не найдено в библиотеке (skipped, не violation)",
            ],
          };
        }
        const result = await ctx.deps.runTambourSize!({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          minSideMm: limit.minSideMm,
          source: limit.source,
          nearLimitToleranceMm: 50,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeTambourSizeFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
          source: result.source,
          minSideMm: result.minSideMm,
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "room_area_min": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — площади помещений пропускаем.",
            },
            warnings: ["room_area_min: нет подключения к библиотеке норм"],
          };
        }
        const limits = ctx.deps.resolveRoomAreaLimits!(ctx.deps.db);
        if (limits.length === 0) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет числовых норм площади помещений — сделайте seed.",
            },
            warnings: [
              "room_area_min: правила площади не найдены в библиотеке (skipped, не violation)",
            ],
          };
        }
        const result = await ctx.deps.runRoomArea!({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          limits,
          nearLimitToleranceM2: 0.5,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeRoomAreaFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "room_height_min": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — высоту помещений пропускаем.",
            },
            warnings: ["room_height_min: нет подключения к библиотеке норм"],
          };
        }
        const limit = ctx.deps.resolveRoomHeight!(ctx.deps.db);
        if (!limit) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет числовой нормы высоты помещения — сделайте seed.",
            },
            warnings: [
              "room_height_min: правило высоты не найдено в библиотеке (skipped, не violation)",
            ],
          };
        }
        const result = await ctx.deps.runRoomHeight!({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          minHeightMm: limit.minHeightMm,
          source: limit.source,
          nearLimitToleranceMm: 50,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeRoomHeightFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
          source: result.source,
          minHeightMm: result.minHeightMm,
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "storey_height": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — высоту этажа пропускаем.",
            },
            warnings: ["storey_height: нет подключения к библиотеке норм"],
          };
        }
        const limit = ctx.deps.resolveStoreyHeight!(ctx.deps.db);
        if (!limit) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет числовой нормы высоты этажа — сделайте seed.",
            },
            warnings: [
              "storey_height: правило высоты этажа не найдено в библиотеке (skipped, не violation)",
            ],
          };
        }
        const result = await ctx.deps.runStoreyHeight!({
          minStoreyHeightMm: limit.minStoreyHeightMm,
          source: limit.source,
          nearLimitToleranceMm: 50,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeStoreyHeightFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
          source: result.source,
          minStoreyHeightMm: result.minStoreyHeightMm,
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "window_sill_height": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — высоту подоконника пропускаем.",
            },
            warnings: ["window_sill_height: нет подключения к библиотеке норм"],
          };
        }
        const limit = ctx.deps.resolveWindowSill!(ctx.deps.db);
        if (!limit) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет числовой нормы высоты подоконника — сделайте seed или передайте minSillHeightMm.",
            },
            warnings: [
              "window_sill_height: правило высоты подоконника не найдено в библиотеке (skipped, не violation)",
            ],
          };
        }
        const result = await ctx.deps.runWindowSill!({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          minSillHeightMm: limit.minSillHeightMm,
          source: limit.source,
          nearLimitToleranceMm: 50,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeWindowSillFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
          source: result.source,
          minSillHeightMm: result.minSillHeightMm,
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "opening_height": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — высоту проёма пропускаем.",
            },
            warnings: ["opening_height: нет подключения к библиотеке норм"],
          };
        }
        const limit = ctx.deps.resolveOpeningHeight!(ctx.deps.db);
        if (!limit) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет числовой нормы высоты проёма — сделайте seed.",
            },
            warnings: [
              "opening_height: правило высоты проёма не найдено в библиотеке (skipped, не violation)",
            ],
          };
        }
        const result = await ctx.deps.runOpeningHeight!({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          minHeightMm: limit.minHeightMm,
          source: limit.source,
          nearLimitToleranceMm: 50,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeOpeningHeightFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
          source: result.source,
          minHeightMm: result.minHeightMm,
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "stair_width": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — ширину марша пропускаем.",
            },
            warnings: ["stair_width: нет подключения к библиотеке норм"],
          };
        }
        const limit = ctx.deps.resolveStairWidth!(ctx.deps.db);
        if (!limit) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет числовой нормы ширины марша — сделайте seed или extract 3.06.",
            },
            warnings: [
              "stair_width: правило ширины марша не найдено в библиотеке (skipped, не violation)",
            ],
          };
        }
        const result = await ctx.deps.runStairWidth!({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          minWidthMm: limit.minWidthMm,
          source: limit.source,
          nearLimitToleranceMm: 50,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeStairWidthFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
          source: result.source,
          minWidthMm: result.minWidthMm,
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "stair_riser_tread": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — подступенок/проступь пропускаем.",
            },
            warnings: ["stair_riser_tread: нет подключения к библиотеке норм"],
          };
        }
        const limits = ctx.deps.resolveStairRiserTread!(ctx.deps.db);
        if (!limits) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет норм подступенка/проступи — сделайте seed (3.06).",
            },
            warnings: [
              "stair_riser_tread: правила riser/tread не найдены (skipped, не violation)",
            ],
          };
        }
        const source =
          limits.riserSource ??
          limits.treadSource ??
          toAuditSource(undefined, "Норма ступени из библиотеки.");
        const result = await ctx.deps.runStairRiserTread!({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          maxRiserMm: limits.maxRiserMm,
          minTreadMm: limits.minTreadMm,
          source,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeStairRiserTreadFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
          riserSource: limits.riserSource,
          treadSource: limits.treadSource,
          fallbackSource: source,
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "ramp_slope_width": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — пандусы пропускаем.",
            },
            warnings: ["ramp_slope_width: нет подключения к библиотеке норм"],
          };
        }
        const limits = ctx.deps.resolveRamp!(ctx.deps.db);
        if (!limits) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет норм ширины/уклона пандуса — сделайте seed (3.06).",
            },
            warnings: [
              "ramp_slope_width: правила пандуса не найдены (skipped, не violation)",
            ],
          };
        }
        const source =
          limits.widthSource ??
          limits.slopeSource ??
          toAuditSource(undefined, "Норма пандуса из библиотеки.");
        const result = await ctx.deps.runRamp!({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          minWidthMm: limits.minWidthMm,
          maxSlopePercent: limits.maxSlopePercent,
          source,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeRampFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
          widthSource: limits.widthSource,
          slopeSource: limits.slopeSource,
          fallbackSource: source,
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      case "railing_height": {
        if (!ctx.deps.db) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message: "База норм недоступна — ограждения пропускаем.",
            },
            warnings: ["railing_height: нет подключения к библиотеке норм"],
          };
        }
        const limit = ctx.deps.resolveRailingHeight!(ctx.deps.db);
        if (!limit) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "skipped",
              checkedCount: 0,
              message:
                "В библиотеке нет числовой нормы высоты ограждения — сделайте seed.",
            },
            warnings: [
              "railing_height: правило высоты ограждения не найдено (skipped, не violation)",
            ],
          };
        }
        const result = await ctx.deps.runRailingHeight!({
          levelName: ctx.levelName,
          includeCompliant: ctx.includeCompliant,
          minHeightMm: limit.minHeightMm,
          source: limit.source,
        });
        warnings.push(...result.warnings);
        if (!result.success) {
          return {
            findings: [],
            check: {
              checkType: checker.checkType,
              status: "error",
              checkedCount: 0,
              message: result.message,
            },
            warnings,
          };
        }
        const findings = normalizeRailingHeightFindings({
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: ctx.includeCompliant ? result.compliant : [],
          source: result.source,
          minHeightMm: result.minHeightMm,
        });
        return {
          findings,
          check: {
            checkType: checker.checkType,
            status: "ok",
            checkedCount: result.totalChecked,
            message: result.message,
          },
          warnings,
        };
      }
      default: {
        const _exhaustive: never = checker.checkType;
        return {
          findings: [],
          check: {
            checkType: _exhaustive,
            status: "skipped",
            checkedCount: 0,
            message: "Unknown checker",
          },
          warnings,
        };
      }
    }
  } catch (error) {
    return {
      findings: [],
      check: {
        checkType: checker.checkType,
        status: "error",
        checkedCount: 0,
        message: error instanceof Error ? error.message : String(error),
      },
      warnings,
    };
  }
}

/**
 * Phase 1 orchestrator: run existing check_* cores, normalize to one report.
 */
export async function runNormAudit(
  options: RunNormAuditOptions = {},
  deps: NormAuditDeps = {}
): Promise<NormAuditResult> {
  const scope = options.scope ?? "floor";
  const mode = options.mode ?? "report";
  const includeCompliant = options.includeCompliant ?? false;
  const nearLimitToleranceMm = options.nearLimitToleranceMm ?? 100;
  const topics = options.topics;

  const resolveLevel =
    deps.resolveLevelName ?? resolveLevelNameFromView;
  let levelName = (options.levelName ?? "").trim();
  if (!levelName && scope === "floor") {
    levelName = (await resolveLevel()).trim();
  }
  if (scope === "project") {
    levelName = "";
  }

  const checkers = selectPhase1Checkers(topics);
  const skippedRules = selectSkippedRules(topics);

  const resolvedDeps = {
    db: deps.db,
    runEvacuation: deps.runEvacuation ?? runEvacuationWidthCheck,
    runRoomDepth: deps.runRoomDepth ?? runRoomDepthCheck,
    runMinDimensions: deps.runMinDimensions ?? runMinDimensionsCheck,
    runFireDoors: deps.runFireDoors ?? runFireDoorsCheck,
    runDoorWidth: deps.runDoorWidth ?? runDoorWidthCheck,
    runTambourSize: deps.runTambourSize ?? runTambourSizeCheck,
    runRoomArea: deps.runRoomArea ?? runRoomAreaCheck,
    runRoomHeight: deps.runRoomHeight ?? runRoomHeightCheck,
    runStoreyHeight: deps.runStoreyHeight ?? runStoreyHeightCheck,
    runWindowSill: deps.runWindowSill ?? runWindowSillCheck,
    runOpeningHeight: deps.runOpeningHeight ?? runOpeningHeightCheck,
    runStairWidth: deps.runStairWidth ?? runStairWidthCheck,
    runStairRiserTread: deps.runStairRiserTread ?? runStairRiserTreadCheck,
    runRamp: deps.runRamp ?? runRampCheck,
    runRailingHeight: deps.runRailingHeight ?? runRailingHeightCheck,
    resolveDepthLimit:
      deps.resolveDepthLimit ?? resolveRoomDepthLimitFromLibrary,
    resolveDoorWidth:
      deps.resolveDoorWidth ?? resolveDoorWidthLimitFromLibrary,
    resolveTambourSize:
      deps.resolveTambourSize ?? resolveTambourSizeLimitFromLibrary,
    resolveRoomAreaLimits:
      deps.resolveRoomAreaLimits ?? resolveRoomAreaLimitsFromLibrary,
    resolveRoomHeight:
      deps.resolveRoomHeight ?? resolveRoomHeightLimitFromLibrary,
    resolveStoreyHeight:
      deps.resolveStoreyHeight ?? resolveStoreyHeightLimitFromLibrary,
    resolveWindowSill:
      deps.resolveWindowSill ?? resolveWindowSillLimitFromLibrary,
    resolveOpeningHeight:
      deps.resolveOpeningHeight ?? resolveOpeningHeightLimitFromLibrary,
    resolveStairWidth:
      deps.resolveStairWidth ?? resolveStairWidthLimitFromLibrary,
    resolveStairRiserTread:
      deps.resolveStairRiserTread ?? resolveStairRiserTreadLimitsFromLibrary,
    resolveRamp: deps.resolveRamp ?? resolveRampLimitsFromLibrary,
    resolveRailingHeight:
      deps.resolveRailingHeight ?? resolveRailingHeightLimitFromLibrary,
    highlight: deps.highlight ?? highlightAuditViolations,
  };

  const findings: NormAuditFinding[] = [];
  const checks: NormAuditCheckRunSummary[] = [];
  const warnings: string[] = [];

  for (const checker of checkers) {
    const part = await runOneChecker(checker, {
      levelName,
      includeCompliant,
      nearLimitToleranceMm,
      deps: resolvedDeps,
    });
    findings.push(...part.findings);
    checks.push(part.check);
    warnings.push(...part.warnings);
  }

  // Deduplicate warning noise from default PDF lists — keep unique
  const uniqueWarnings = [...new Set(warnings)];

  const counts = summarizeFindings(findings, skippedRules.length);
  const checksRun = checks.filter((c) => c.status === "ok").length;
  const checksFailed = checks.filter((c) => c.status === "error").length;

  let highlightedCount: number | undefined;
  let filledRegionCount: number | undefined;
  let doorHighlightCount: number | undefined;
  if (mode === "highlight") {
    try {
      const painted = await resolvedDeps.highlight({
        findings,
      });
      highlightedCount = painted.highlightedCount;
      filledRegionCount = painted.filledRegionCount;
      doorHighlightCount = painted.doorCount;
      if (painted.message) uniqueWarnings.push(painted.message);
    } catch (error) {
      uniqueWarnings.push(
        `Подсветка не удалась: ${
          error instanceof Error ? error.message : String(error)
        }`
      );
    }
  }

  const summary = {
    ...counts,
    checksRun,
    checksFailed,
  };

  const messageParts = [
    `Нормоконтроль: нарушений ${summary.violations}`,
    summary.nearLimit > 0 ? `пограничных ${summary.nearLimit}` : null,
    `проверок ${summary.checksRun}/${checkers.length}`,
    skippedRules.length > 0 ? `skipped ${skippedRules.length}` : null,
  ].filter(Boolean);

  const result: NormAuditResult = {
    success: checksFailed === 0 || findings.length > 0 || checksRun > 0,
    message: messageParts.join(", ") + ".",
    scope,
    levelName,
    mode,
    scopeNote: AUDIT_SCOPE_NOTE,
    summary,
    findings,
    skippedRules: [...skippedRules],
    checks,
    highlightedCount,
    filledRegionCount,
    doorHighlightCount,
    warnings: uniqueWarnings,
  };

  // success=false only when every selected checker failed and nothing ran
  if (checkers.length > 0 && checksRun === 0 && checksFailed === checkers.length) {
    result.success = false;
    result.message = "Нормоконтроль не выполнен: все выбранные проверки завершились ошибкой.";
  }

  return result;
}

export { formatNormAuditReport };
