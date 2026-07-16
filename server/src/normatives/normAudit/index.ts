export { AUDIT_SCOPE_NOTE, PHASE1_CHECKERS, PHASE2_SKIPPED } from "./checklist.js";
export { formatNormAuditReport } from "./formatAuditReport.js";
export {
  findingsForHighlight,
  formatFindingNote,
  normalizeEvacuationFindings,
  normalizeFireDoorFindings,
  normalizeMinDimensionFindings,
  normalizeRoomDepthFindings,
  summarizeFindings,
} from "./normalizeFindings.js";
export {
  runNormAudit,
  type NormAuditDeps,
  type RunNormAuditOptions,
} from "./runNormAudit.js";
export { resolveRoomDepthLimitFromLibrary } from "./resolveDepthLimit.js";
export type {
  NormAuditCheckType,
  NormAuditFinding,
  NormAuditResult,
  NormAuditSkippedRule,
  NormAuditSource,
  NormAuditSummary,
} from "./types.js";
