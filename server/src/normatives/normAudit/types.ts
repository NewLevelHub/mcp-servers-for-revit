import type { NormativeSourceRef } from "../types.js";

/** Measurable checkers available in Phase 1 (+ reserved Phase 2 ids). */
export type NormAuditCheckType =
  | "evacuation_width"
  | "room_depth"
  | "min_dimensions"
  | "fire_doors"
  | "door_clear_width"
  | "tambour_size_min"
  | "room_area_min"
  | "room_height_min"
  | "storey_height"
  | "window_sill_height"
  | "opening_height"
  | "stair_width"
  | "stair_riser_tread"
  | "ramp_slope_width"
  | "railing_height"
  | "egress_opening_width"
  | "passage_width"
  // МГН accessibility (СП РК 3.06-101-2012*).
  | "mgn_room_geometry"
  | "mgn_turning_circle"
  | "mgn_corridor_width"
  | "mgn_wc_dimensions"
  | "mgn_door_width"
  | "mgn_ramp_slope"
  | "mgn_door_maneuvering";

export type NormAuditFindingStatus =
  | "violation"
  | "nearLimit"
  | "compliant"
  | "skipped";

export interface NormAuditSource {
  document: string;
  clause: string;
  quote: string;
  page?: number;
}

export interface NormAuditFinding {
  checkType: NormAuditCheckType;
  status: NormAuditFindingStatus;
  elementId: number;
  uniqueId?: string;
  name: string;
  level: string;
  /** Human metric label, e.g. «ширина», «глубина», «ПД». */
  metric?: string;
  /** Unit used by actual/required values; defaults to millimetres. */
  unit?: "mm" | "m2" | "percent";
  actualMm?: number | null;
  requiredMm?: number | null;
  deviationMm?: number | null;
  note?: string;
  source: NormAuditSource;
}

export interface NormAuditSkippedRule {
  checkType: NormAuditCheckType;
  reason: string;
  /** Topics that would have selected this check. */
  topics: string[];
}

export interface NormAuditCheckRunSummary {
  checkType: NormAuditCheckType;
  status: "ok" | "error" | "skipped";
  checkedCount: number;
  message?: string;
}

export interface NormAuditSummary {
  violations: number;
  nearLimit: number;
  compliant: number;
  skipped: number;
  checksRun: number;
  checksFailed: number;
}

export interface NormAuditResult {
  success: boolean;
  message: string;
  scope: "floor" | "project";
  levelName: string;
  mode: "report" | "highlight";
  /** Explicit disclaimer for the customer / agent. */
  scopeNote: string;
  summary: NormAuditSummary;
  findings: NormAuditFinding[];
  skippedRules: NormAuditSkippedRule[];
  checks: NormAuditCheckRunSummary[];
  highlightedCount?: number;
  /** Rooms painted with Filled Region when mode=highlight. */
  filledRegionCount?: number;
  /** Fire doors painted with Override when mode=highlight. */
  doorHighlightCount?: number;
  /** Ramps and other non-Room/non-door elements painted with Override. */
  otherElementHighlightCount?: number;
  warnings: string[];
}

export function toAuditSource(
  source: NormativeSourceRef | NormAuditSource | null | undefined,
  fallbackQuote = "Источник нормы не указан."
): NormAuditSource {
  if (!source) {
    return { document: "—", clause: "", quote: fallbackQuote };
  }
  return {
    document: source.document || "—",
    clause: source.clause || "",
    quote: source.quote || fallbackQuote,
    ...(source.page != null ? { page: source.page } : {}),
  };
}
