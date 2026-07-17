export { AUDIT_SCOPE_NOTE, PHASE1_CHECKERS, PHASE2_SKIPPED } from "./checklist.js";
export { formatNormAuditReport } from "./formatAuditReport.js";
export {
  formatFindingAnnotation,
  findingsToAnnotationNotes,
} from "./formatFindingAnnotation.js";
export {
  findingsForHighlight,
  formatFindingNote,
  normalizeDoorWidthFindings,
  normalizeEvacuationFindings,
  normalizeFireDoorFindings,
  normalizeMinDimensionFindings,
  normalizeRoomDepthFindings,
  normalizeTambourSizeFindings,
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
  resolveTambourSizeLimitFromLibrary,
  type ResolvedTambourSizeLimit,
} from "./resolveTambourSize.js";
export {
  classifyDoorWidths,
  isDoorAccessory,
  type DoorWidthInput,
  type DoorWidthClassification,
} from "./doorWidth.js";
export {
  classifyTambourSizes,
  isTambourRoom,
  type TambourRoomInput,
  type TambourSizeClassification,
} from "./tambourSize.js";
export {
  runDoorWidthCheck,
  runRoomAreaCheck,
  runRoomHeightCheck,
  runStoreyHeightCheck,
  runTambourSizeCheck,
  type DoorWidthRunnerResult,
  type TambourSizeRunnerResult,
} from "./runners.js";
export {
  classifyResidentialRoom,
  isResidentialRoomForHeight,
  isLivingRoomForDepth,
  isLivingScopeAlias,
  type ResidentialRoomCategory,
} from "./roomPurpose.js";
export {
  classifyRoomAreas,
  type RoomAreaClassification,
  type RoomAreaInput,
} from "./roomArea.js";
export {
  classifyRoomHeights,
  type RoomHeightClassification,
  type RoomHeightInput,
} from "./roomHeight.js";
export {
  classifyStoreyHeights,
  computeStoreyHeights,
  type LevelInput,
  type StoreyHeightClassification,
} from "./storeyHeight.js";
export {
  resolveRoomAreaLimitsFromLibrary,
  type RoomAreaLimit,
} from "./resolveRoomAreaLimits.js";
export {
  resolveRoomHeightLimitFromLibrary,
  type ResolvedRoomHeightLimit,
} from "./resolveRoomHeightLimit.js";
export {
  resolveStoreyHeightLimitFromLibrary,
  type ResolvedStoreyHeightLimit,
} from "./resolveStoreyHeightLimit.js";
export {
  normalizeRoomAreaFindings,
  normalizeRoomHeightFindings,
  normalizeStoreyHeightFindings,
} from "./normalizeFindings.js";
export type {
  NormAuditCheckType,
  NormAuditFinding,
  NormAuditResult,
  NormAuditSkippedRule,
  NormAuditSource,
  NormAuditSummary,
} from "./types.js";
