export { AUDIT_SCOPE_NOTE, PHASE1_CHECKERS, PHASE2_SKIPPED } from "./checklist.js";
export { formatNormAuditReport } from "./formatAuditReport.js";
export {
  findingsForHighlight,
  formatFindingNote,
  normalizeDoorWidthFindings,
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
export {
  resolveDoorWidthLimitFromLibrary,
  type ResolvedDoorWidthLimit,
} from "./resolveDoorWidth.js";
export {
  classifyDoorWidths,
  isDoorAccessory,
  type DoorWidthInput,
  type DoorWidthClassification,
} from "./doorWidth.js";
export { runDoorWidthCheck, type DoorWidthRunnerResult } from "./runners.js";
export type {
  NormAuditCheckType,
  NormAuditFinding,
  NormAuditResult,
  NormAuditSkippedRule,
  NormAuditSource,
  NormAuditSummary,
} from "./types.js";
