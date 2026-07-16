import type {
  NormAuditCheckType,
  NormAuditFinding,
  NormAuditSource,
} from "./types.js";
import { toAuditSource } from "./types.js";

function roundMm(value: number | null | undefined): number | undefined {
  if (value == null || Number.isNaN(value)) return undefined;
  return Math.round(value);
}

function roundAreaM2(value: number): number {
  return Math.round(value * 1000) / 1000;
}

function nameOf(item: {
  name?: string;
  number?: string;
  mark?: string;
  id: number;
}): string {
  return (
    (item.name && item.name.trim()) ||
    (item.number && item.number.trim()) ||
    (item.mark && item.mark.trim()) ||
    `id ${item.id}`
  );
}

export interface EvacuationWidthItemInput {
  id: number;
  uniqueId?: string;
  name?: string;
  number?: string;
  level?: string;
  actualWidthMm: number;
  requiredWidthMm: number;
  deviationMm: number;
  isCompliant: boolean;
}

export function normalizeEvacuationFindings(input: {
  violations: EvacuationWidthItemInput[];
  nearLimit: EvacuationWidthItemInput[];
  compliant: EvacuationWidthItemInput[];
  source: NormAuditSource | null | undefined;
  minWidthMm: number;
}): NormAuditFinding[] {
  const source = toAuditSource(
    input.source,
    `Минимальная ширина эвак. коридора: ${input.minWidthMm} мм.`
  );
  const findings: NormAuditFinding[] = [];

  const push = (
    item: EvacuationWidthItemInput,
    status: "violation" | "nearLimit" | "compliant"
  ) => {
    findings.push({
      checkType: "evacuation_width",
      status,
      elementId: item.id,
      uniqueId: item.uniqueId,
      name: nameOf(item),
      level: item.level ?? "",
      metric: "ширина",
      actualMm: roundMm(item.actualWidthMm),
      requiredMm: roundMm(item.requiredWidthMm || input.minWidthMm),
      deviationMm: roundMm(item.deviationMm),
      source,
    });
  };

  for (const item of input.violations) push(item, "violation");
  for (const item of input.nearLimit) push(item, "nearLimit");
  for (const item of input.compliant) push(item, "compliant");
  return findings;
}

export interface RoomDepthItemInput {
  id: number;
  uniqueId?: string;
  name?: string;
  number?: string;
  level?: string;
  depthMm: number;
  isCompliant: boolean;
  deviationMm?: number;
}

export function normalizeRoomDepthFindings(input: {
  violations: RoomDepthItemInput[];
  compliant: RoomDepthItemInput[];
  source: NormAuditSource | null | undefined;
  minDepthMm?: number;
  maxDepthMm?: number;
}): NormAuditFinding[] {
  const required =
    input.maxDepthMm ?? input.minDepthMm ?? undefined;
  const source = toAuditSource(
    input.source,
    required != null
      ? `Глубина помещения: ${input.minDepthMm != null ? `≥ ${input.minDepthMm} мм` : ""}${input.minDepthMm != null && input.maxDepthMm != null ? ", " : ""}${input.maxDepthMm != null ? `≤ ${input.maxDepthMm} мм` : ""}`.trim()
      : "Лимит глубины помещения."
  );

  const findings: NormAuditFinding[] = [];
  const push = (
    item: RoomDepthItemInput,
    status: "violation" | "compliant"
  ) => {
    findings.push({
      checkType: "room_depth",
      status,
      elementId: item.id,
      uniqueId: item.uniqueId,
      name: nameOf(item),
      level: item.level ?? "",
      metric: "глубина",
      actualMm: roundMm(item.depthMm),
      requiredMm: required != null ? roundMm(required) : undefined,
      deviationMm: roundMm(item.deviationMm),
      source,
    });
  };

  for (const item of input.violations) push(item, "violation");
  for (const item of input.compliant) push(item, "compliant");
  return findings;
}

export interface MinDimensionItemInput {
  id: number;
  uniqueId?: string;
  name?: string;
  number?: string;
  level?: string;
  spaceKind?: string;
  metric?: string;
  actualValueMm: number;
  requiredValueMm: number;
  deviationMm: number;
  isCompliant: boolean;
}

const MIN_DIM_METRIC_LABELS: Record<string, string> = {
  width: "ширина",
  depth: "глубина",
  pier_to_opening: "простенок до проёма",
  pier_between_openings: "простенок между проёмами",
};

export function normalizeMinDimensionFindings(input: {
  violations: MinDimensionItemInput[];
  compliant: MinDimensionItemInput[];
  /** Per-item source preferred; fallback used when missing. */
  sourceForItem?: (item: MinDimensionItemInput) => NormAuditSource | null | undefined;
  fallbackSource?: NormAuditSource | null;
}): NormAuditFinding[] {
  const findings: NormAuditFinding[] = [];

  const push = (
    item: MinDimensionItemInput,
    status: "violation" | "compliant"
  ) => {
    const metricKey = item.metric ?? "";
    const source = toAuditSource(
      input.sourceForItem?.(item) ?? input.fallbackSource,
      `Норма: ${Math.round(item.requiredValueMm)} мм (${metricKey || "размер"}).`
    );
    findings.push({
      checkType: "min_dimensions",
      status,
      elementId: item.id,
      uniqueId: item.uniqueId,
      name: nameOf(item),
      level: item.level ?? "",
      metric:
        MIN_DIM_METRIC_LABELS[metricKey] ??
        (metricKey || item.spaceKind || undefined),
      actualMm: roundMm(item.actualValueMm),
      requiredMm: roundMm(item.requiredValueMm),
      deviationMm: roundMm(item.deviationMm),
      note: item.spaceKind,
      source,
    });
  };

  for (const item of input.violations) push(item, "violation");
  for (const item of input.compliant) push(item, "compliant");
  return findings;
}

export interface FireDoorItemInput {
  id: number;
  uniqueId?: string;
  mark?: string;
  type?: string;
  family?: string;
  level?: string;
  fromRoom?: string;
  toRoom?: string;
  requiresFireDoor: boolean;
  compliant: boolean;
  reason?: string;
  source: NormAuditSource;
  markSource?: string;
  openingWidthMm?: number | null;
}

export function normalizeFireDoorFindings(
  doors: FireDoorItemInput[]
): NormAuditFinding[] {
  const findings: NormAuditFinding[] = [];

  for (const door of doors) {
    if (!door.requiresFireDoor) continue;
    const name =
      (door.mark && door.mark.trim()) ||
      (door.type && door.type.trim()) ||
      `дверь ${door.id}`;
    findings.push({
      checkType: "fire_doors",
      status: door.compliant ? "compliant" : "violation",
      elementId: door.id,
      uniqueId: door.uniqueId,
      name,
      level: door.level ?? "",
      metric: "ПД",
      actualMm: door.openingWidthMm != null ? roundMm(door.openingWidthMm) : undefined,
      requiredMm: undefined,
      note: [
        door.reason,
        door.fromRoom || door.toRoom
          ? `${door.fromRoom || "—"} → ${door.toRoom || "—"}`
          : undefined,
        door.markSource && door.markSource !== "none"
          ? `источник ПД: ${door.markSource}`
          : undefined,
      ]
        .filter(Boolean)
        .join("; "),
      source: toAuditSource(door.source),
    });
  }

  return findings;
}

export interface DoorWidthItemInput {
  id: number;
  uniqueId?: string;
  family?: string;
  type?: string;
  level?: string;
  status: "violation" | "nearLimit" | "compliant";
  actualMm: number;
  requiredMm: number;
  deviationMm: number;
  isOnEgressPath?: boolean;
}

function doorNameOf(item: {
  type?: string;
  family?: string;
  id: number;
}): string {
  return (
    (item.type && item.type.trim()) ||
    (item.family && item.family.trim()) ||
    `дверь ${item.id}`
  );
}

export function normalizeDoorWidthFindings(input: {
  violations: DoorWidthItemInput[];
  nearLimit: DoorWidthItemInput[];
  compliant: DoorWidthItemInput[];
  source: NormAuditSource | null | undefined;
  minWidthMm: number;
}): NormAuditFinding[] {
  const source = toAuditSource(
    input.source,
    `Минимальная ширина двери/проёма: ${input.minWidthMm} мм.`
  );
  const findings: NormAuditFinding[] = [];

  const push = (item: DoorWidthItemInput) => {
    findings.push({
      checkType: "door_clear_width",
      status: item.status,
      elementId: item.id,
      uniqueId: item.uniqueId,
      name: doorNameOf(item),
      level: item.level ?? "",
      metric: "ширина",
      actualMm: roundMm(item.actualMm),
      requiredMm: roundMm(item.requiredMm || input.minWidthMm),
      deviationMm: roundMm(item.deviationMm),
      note: item.isOnEgressPath ? "на пути эвакуации" : undefined,
      source,
    });
  };

  for (const item of input.violations) push(item);
  for (const item of input.nearLimit) push(item);
  for (const item of input.compliant) push(item);
  return findings;
}

export interface TambourSizeItemInput {
  id: number;
  uniqueId?: string;
  name: string;
  level?: string;
  status: "violation" | "nearLimit" | "compliant";
  widthMm: number;
  depthMm: number;
  minSideMm: number;
  requiredSideMm: number;
  deviationMm: number;
}

export function normalizeTambourSizeFindings(input: {
  violations: TambourSizeItemInput[];
  nearLimit: TambourSizeItemInput[];
  compliant: TambourSizeItemInput[];
  source: NormAuditSource | null | undefined;
  minSideMm: number;
}): NormAuditFinding[] {
  const source = toAuditSource(
    input.source,
    `Минимальный габарит входного тамбура: ${input.minSideMm} × ${input.minSideMm} мм.`
  );
  const findings: NormAuditFinding[] = [];

  const push = (item: TambourSizeItemInput) => {
    const required = item.requiredSideMm || input.minSideMm;
    findings.push({
      checkType: "tambour_size_min",
      status: item.status,
      elementId: item.id,
      uniqueId: item.uniqueId,
      name: item.name,
      level: item.level ?? "",
      metric: "габарит тамбура",
      actualMm: roundMm(item.minSideMm),
      requiredMm: roundMm(required),
      deviationMm: roundMm(item.deviationMm),
      note:
        `ширина ${roundMm(item.widthMm)} мм × глубина ${roundMm(item.depthMm)} мм ` +
        `(норма ≥ ${roundMm(required)} × ${roundMm(required)} мм)`,
      source,
    });
  };

  for (const item of input.violations) push(item);
  for (const item of input.nearLimit) push(item);
  for (const item of input.compliant) push(item);
  return findings;
}

const CATEGORY_LABELS: Record<string, string> = {
  living_room: "жилая",
  kitchen: "кухня",
  bathroom: "санузел",
  bedroom: "спальня",
};

export function normalizeRoomAreaFindings(input: {
  violations: Array<{
    id: number;
    uniqueId?: string;
    name: string;
    level?: string;
    status: "violation" | "nearLimit" | "compliant";
    areaM2: number;
    requiredAreaM2: number;
    deviationM2: number;
    category: string;
    limitSource: { source: NormAuditSource };
  }>;
  nearLimit: typeof input.violations;
  compliant: typeof input.violations;
}): NormAuditFinding[] {
  const findings: NormAuditFinding[] = [];

  const push = (item: (typeof input.violations)[number]) => {
    const cat = CATEGORY_LABELS[item.category] ?? item.category;
    findings.push({
      checkType: "room_area_min",
      status: item.status,
      elementId: item.id,
      uniqueId: item.uniqueId,
      name: item.name,
      level: item.level ?? "",
      metric: "площадь",
      actualMm: roundAreaM2(item.areaM2),
      requiredMm: roundAreaM2(item.requiredAreaM2),
      deviationMm: roundMm(item.deviationM2 * 1000),
      note: `${cat}: ${roundAreaM2(item.areaM2)} м² (норма ≥ ${roundAreaM2(item.requiredAreaM2)} м²)`,
      source: toAuditSource(item.limitSource.source),
    });
  };

  for (const item of input.violations) push(item);
  for (const item of input.nearLimit) push(item);
  for (const item of input.compliant) push(item);
  return findings;
}

export function normalizeRoomHeightFindings(input: {
  violations: Array<{
    id: number;
    uniqueId?: string;
    name: string;
    level?: string;
    status: "violation" | "nearLimit" | "compliant";
    actualHeightMm: number;
    requiredHeightMm: number;
    deviationMm: number;
  }>;
  nearLimit: typeof input.violations;
  compliant: typeof input.violations;
  source: NormAuditSource | null | undefined;
  minHeightMm: number;
}): NormAuditFinding[] {
  const source = toAuditSource(
    input.source,
    `Минимальная высота помещения: ${input.minHeightMm} мм.`
  );
  const findings: NormAuditFinding[] = [];

  const push = (item: (typeof input.violations)[number]) => {
    findings.push({
      checkType: "room_height_min",
      status: item.status,
      elementId: item.id,
      uniqueId: item.uniqueId,
      name: item.name,
      level: item.level ?? "",
      metric: "высота",
      actualMm: roundMm(item.actualHeightMm),
      requiredMm: roundMm(item.requiredHeightMm),
      deviationMm: roundMm(item.deviationMm),
      source,
    });
  };

  for (const item of input.violations) push(item);
  for (const item of input.nearLimit) push(item);
  for (const item of input.compliant) push(item);
  return findings;
}

export function normalizeStoreyHeightFindings(input: {
  violations: Array<{
    id: number;
    name: string;
    lowerLevel: string;
    upperLevel: string;
    status: "violation" | "nearLimit" | "compliant";
    actualHeightMm: number;
    requiredHeightMm: number;
    deviationMm: number;
  }>;
  nearLimit: typeof input.violations;
  compliant: typeof input.violations;
  source: NormAuditSource | null | undefined;
  minStoreyHeightMm: number;
}): NormAuditFinding[] {
  const source = toAuditSource(
    input.source,
    `Минимальная высота этажа: ${input.minStoreyHeightMm} мм.`
  );
  const findings: NormAuditFinding[] = [];

  const push = (item: (typeof input.violations)[number]) => {
    findings.push({
      checkType: "storey_height",
      status: item.status,
      elementId: item.id,
      name: item.name,
      level: item.lowerLevel,
      metric: "высота этажа",
      actualMm: roundMm(item.actualHeightMm),
      requiredMm: roundMm(item.requiredHeightMm),
      deviationMm: roundMm(item.deviationMm),
      note: `${item.lowerLevel} → ${item.upperLevel}`,
      source,
    });
  };

  for (const item of input.violations) push(item);
  for (const item of input.nearLimit) push(item);
  for (const item of input.compliant) push(item);
  return findings;
}

export function summarizeFindings(
  findings: NormAuditFinding[],
  skippedCount: number
): {
  violations: number;
  nearLimit: number;
  compliant: number;
  skipped: number;
} {
  let violations = 0;
  let nearLimit = 0;
  let compliant = 0;
  for (const finding of findings) {
    if (finding.status === "violation") violations += 1;
    else if (finding.status === "nearLimit") nearLimit += 1;
    else if (finding.status === "compliant") compliant += 1;
  }
  return { violations, nearLimit, compliant, skipped: skippedCount };
}

export function findingsForHighlight(
  findings: NormAuditFinding[]
): Array<{ elementId: number; note: string }> {
  return findings
    .filter(
      (f) => f.status === "violation" || f.status === "nearLimit"
    )
    .map((f) => ({
      elementId: f.elementId,
      note: formatFindingNote(f),
    }));
}

export function formatFindingNote(finding: NormAuditFinding): string {
  const metric = finding.metric ? `${finding.metric}: ` : "";
  if (finding.checkType === "fire_doors") {
    return finding.note || "Требуется противопожарная дверь";
  }
  if (finding.checkType === "room_area_min" && finding.actualMm != null && finding.requiredMm != null) {
    return `${metric}${finding.actualMm} м² vs норма ${finding.requiredMm} м²`;
  }
  if (finding.actualMm != null && finding.requiredMm != null) {
    return `${metric}${finding.actualMm} мм vs норма ${finding.requiredMm} мм`;
  }
  return finding.note || `${finding.checkType}: ${finding.status}`;
}

export function primarySourceFromFindings(
  findings: NormAuditFinding[]
): NormAuditSource {
  const first = findings.find((f) => f.source.document && f.source.document !== "—");
  return first?.source ?? { document: "нормоконтроль", clause: "", quote: "" };
}

export type { NormAuditCheckType };
