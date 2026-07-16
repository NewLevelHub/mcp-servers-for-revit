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
  summarizeFindings,
} from "./normalizeFindings.js";
import { resolveRoomDepthLimitFromLibrary } from "./resolveDepthLimit.js";
import { resolveDoorWidthLimitFromLibrary } from "./resolveDoorWidth.js";
import {
  highlightAuditViolations,
  resolveLevelNameFromView,
  runDoorWidthCheck,
  runEvacuationWidthCheck,
  runFireDoorsCheck,
  runMinDimensionsCheck,
  runRoomDepthCheck,
  type DoorWidthRunnerResult,
  type EvacuationWidthRunnerResult,
  type FireDoorsRunnerResult,
  type MinDimensionsRunnerResult,
  type RoomDepthRunnerResult,
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
        | "resolveDepthLimit"
        | "resolveDoorWidth"
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
    resolveDepthLimit:
      deps.resolveDepthLimit ?? resolveRoomDepthLimitFromLibrary,
    resolveDoorWidth:
      deps.resolveDoorWidth ?? resolveDoorWidthLimitFromLibrary,
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
