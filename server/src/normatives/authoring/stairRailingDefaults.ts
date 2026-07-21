import type { Database } from "better-sqlite3";
import type {
  ResolvedRailingHeightLimit,
  ResolvedStairRiserTreadLimits,
  ResolvedStairWidthLimit,
} from "../normAudit/resolveVerticalCirculation.js";
import type { NormAuditSource } from "../normAudit/types.js";

export type PointMm = { x: number; y: number; z?: number };

export interface StairAuthoringInput {
  typeId: number;
  baseLevelId: number;
  topLevelId: number;
  startPoint?: PointMm;
  endPoint?: PointMm;
  layout?: string;
  bearingDeg?: number;
  turn?: string;
  landingDepthMm?: number;
  firstRunLengthMm?: number;
  secondRunLengthMm?: number;
  widthMm?: number;
  riserHeightMm?: number;
  treadDepthMm?: number;
}

export interface RailingAuthoringInput {
  typeId: number;
  hostElementId?: number;
  pathPoints?: PointMm[];
  levelId?: number;
  levelOffsetMm?: number;
  isClosedLoop?: boolean;
  heightMm?: number;
}

export type ResolveResult<T> =
  | { ok: true; value: T }
  | { ok: false; error: string };

const EMPTY_LIBRARY_HINT =
  "Norm library has no matching rule. Call extract_norm_rules_from_pdf with action=seed " +
  "(or topic search), then retry — or pass the dimension explicitly in mm.";

export interface StairResolveDeps {
  resolveWidth: (db: Database) => ResolvedStairWidthLimit | null;
  resolveRiserTread: (db: Database) => ResolvedStairRiserTreadLimits | null;
}

export interface RailingResolveDeps {
  resolveHeight: (db: Database) => ResolvedRailingHeightLimit | null;
}

/**
 * Hybrid defaults for create_stair: explicit widthMm wins;
 * otherwise resolve from library; empty library ? fail.
 */
export function resolveStairAuthoringDefaults(
  db: Database,
  input: StairAuthoringInput,
  deps: StairResolveDeps
): ResolveResult<
  StairAuthoringInput & {
    widthMm: number;
    normSource?: NormAuditSource;
    warnings: string[];
  }
> {
  if (!input.typeId || input.typeId <= 0) {
    return {
      ok: false,
      error:
        "typeId is required. Call get_available_family_types for StairsType and pass a valid typeId.",
    };
  }

  const warnings: string[] = [];
  let widthMm = input.widthMm;
  let normSource: NormAuditSource | undefined;

  if (widthMm == null) {
    const resolved = deps.resolveWidth(db);
    if (!resolved) {
      return {
        ok: false,
        error: `widthMm omitted and ${EMPTY_LIBRARY_HINT}`,
      };
    }
    widthMm = resolved.minWidthMm;
    normSource = resolved.source;
  }

  let riserHeightMm = input.riserHeightMm;
  let treadDepthMm = input.treadDepthMm;
  if (riserHeightMm == null || treadDepthMm == null) {
    const rt = deps.resolveRiserTread(db);
    if (rt) {
      if (riserHeightMm == null && rt.maxRiserMm != null) {
        riserHeightMm = rt.maxRiserMm;
        warnings.push(
          `riserHeightMm defaulted from library (${rt.maxRiserMm} mm max) — v1 uses StairsType actual riser`
        );
      }
      if (treadDepthMm == null && rt.minTreadMm != null) {
        treadDepthMm = rt.minTreadMm;
        warnings.push(
          `treadDepthMm defaulted from library (${rt.minTreadMm} mm min) — v1 uses StairsType actual tread`
        );
      }
    } else if (riserHeightMm == null && treadDepthMm == null) {
      warnings.push(
        "No riser/tread rules in library; StairsType defaults will be used"
      );
    }
  }

  return {
    ok: true,
    value: {
      ...input,
      widthMm,
      riserHeightMm,
      treadDepthMm,
      normSource,
      warnings,
    },
  };
}

/**
 * Hybrid defaults for create_railing: explicit heightMm wins;
 * otherwise resolve from library; empty library ? fail.
 * Also validates host XOR path mode.
 */
export function resolveRailingAuthoringDefaults(
  db: Database,
  input: RailingAuthoringInput,
  deps: RailingResolveDeps
): ResolveResult<
  RailingAuthoringInput & {
    heightMm: number;
    normSource?: NormAuditSource;
    warnings: string[];
  }
> {
  if (!input.typeId || input.typeId <= 0) {
    return {
      ok: false,
      error:
        "typeId is required. Call get_available_family_types for RailingType and pass a valid typeId.",
    };
  }

  const hostMode = input.hostElementId != null && input.hostElementId > 0;
  const pathMode =
    Array.isArray(input.pathPoints) && input.pathPoints.length >= 2;

  if (hostMode && pathMode) {
    return {
      ok: false,
      error: "Provide either hostElementId OR pathPoints+levelId, not both.",
    };
  }

  if (!hostMode && !pathMode) {
    return {
      ok: false,
      error: "Provide hostElementId (stair) or pathPoints (=2) with levelId.",
    };
  }

  if (pathMode && (input.levelId == null || input.levelId <= 0)) {
    return {
      ok: false,
      error: "levelId is required for path mode.",
    };
  }

  const warnings: string[] = [];
  let heightMm = input.heightMm;
  let normSource: NormAuditSource | undefined;

  if (heightMm == null) {
    const resolved = deps.resolveHeight(db);
    if (!resolved) {
      return {
        ok: false,
        error: `heightMm omitted and ${EMPTY_LIBRARY_HINT}`,
      };
    }
    heightMm = resolved.minHeightMm;
    normSource = resolved.source;
    warnings.push(
      `heightMm=${heightMm} from library — select a RailingType that encodes this height (e.g. h ${heightMm}); type params are not mutated`
    );
  }

  return {
    ok: true,
    value: {
      ...input,
      heightMm,
      normSource,
      warnings,
    },
  };
}
