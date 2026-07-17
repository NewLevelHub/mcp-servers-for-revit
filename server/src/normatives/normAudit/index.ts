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
  normalizeOpeningHeightFindings,
  normalizeRoomDepthFindings,
  normalizeTambourSizeFindings,
  normalizeWindowSillFindings,
  normalizeStairWidthFindings,
  normalizeStairRiserTreadFindings,
  normalizeRampFindings,
  normalizeRailingHeightFindings,
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
  resolveWindowSillLimitFromLibrary,
  type ResolvedWindowSillLimit,
} from "./resolveWindowSill.js";
export {
  resolveOpeningHeightLimitFromLibrary,
  type ResolvedOpeningHeightLimit,
} from "./resolveOpeningHeight.js";
export {
  resolveStairWidthLimitFromLibrary,
  resolveStairRiserTreadLimitsFromLibrary,
  resolveRampLimitsFromLibrary,
  resolveRailingHeightLimitFromLibrary,
  type ResolvedStairWidthLimit,
  type ResolvedStairRiserTreadLimits,
  type ResolvedRampLimits,
  type ResolvedRailingHeightLimit,
} from "./resolveVerticalCirculation.js";
export {
  classifyDoorWidths,
  isDoorAccessory,
  type DoorWidthInput,
  type DoorWidthClassification,
} from "./doorWidth.js";
export {
  classifyWindowSills,
  isWindowAccessory,
  type WindowSillInput,
  type WindowSillClassification,
} from "./windowSill.js";
export {
  classifyOpeningHeights,
  type OpeningHeightInput,
  type OpeningHeightClassification,
} from "./openingHeight.js";
export {
  classifyStairWidths,
  classifyStairRiserTreads,
  classifyRamps,
  classifyRailingHeights,
  type StairWidthInput,
  type ClassifiedStairWidth,
  type StairRiserTreadInput,
  type ClassifiedStairRiserTread,
  type RampInput,
  type ClassifiedRamp,
  type RailingInput,
  type ClassifiedRailing,
} from "./verticalCirculation.js";
export {
  classifyTambourSizes,
  isTambourRoom,
  type TambourRoomInput,
  type TambourSizeClassification,
} from "./tambourSize.js";
export {
  runDoorWidthCheck,
  runOpeningHeightCheck,
  runRailingHeightCheck,
  runRampCheck,
  runRoomAreaCheck,
  runRoomHeightCheck,
  runStairRiserTreadCheck,
  runStairWidthCheck,
  runStoreyHeightCheck,
  runTambourSizeCheck,
  runWindowSillCheck,
  type DoorWidthRunnerResult,
  type OpeningHeightRunnerResult,
  type RailingHeightRunnerResult,
  type RampRunnerResult,
  type StairRiserTreadRunnerResult,
  type StairWidthRunnerResult,
  type TambourSizeRunnerResult,
  type WindowSillRunnerResult,
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
