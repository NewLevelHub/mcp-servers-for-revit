/**
 * МГН accessibility checks (REV: нормоконтроль доступности) per
 * СП РК 3.06-101-2012* «Требования по доступности зданий и сооружений
 * для маломобильных групп населения».
 *
 * Quotes below are extracted verbatim from normatives/SP_RK_3.06-101-2012_27.11.2019.pdf.
 * Room geometry comes from get_room_geometry_metrics; clear door widths,
 * door-side maneuvering spaces, and ramp slopes come from get_door_egress_info.
 * Missing trustworthy measurements are emitted as explicit skipped findings.
 */

import { isTambourRoom } from "./tambourSize.js";
import type { NormAuditSource } from "./types.js";

export const MGN_DOCUMENT = "СП РК 3.06-101-2012*";

/** Разворот кресла-коляски — минимальный диаметр зоны. */
export const MGN_TURNING_DIAMETER_MM = 1500;
export const MGN_TURNING_SOURCE: NormAuditSource = {
  document: MGN_DOCUMENT,
  clause: "п. 4.3.2.43",
  quote:
    "Диаметр зоны для самостоятельного разворота на 90 - 180° инвалида на " +
    "кресло-коляске следует принимать не менее 1,5-1,7 м.",
};

/** Коридоры доступных зданий: 1,5 м; для разъезда двух колясок — 1,8 м. */
export const MGN_CORRIDOR_WIDTH_MM = 1500;
export const MGN_EVAC_CORRIDOR_WIDTH_MM = 1800;
export const MGN_CORRIDOR_SOURCE: NormAuditSource = {
  document: MGN_DOCUMENT,
  clause: "п. 4.3.2.38",
  quote:
    "В общественных зданиях, доступных для инвалидов, ширина коридоров должна быть " +
    "не менее 1,5 м. Там, где предполагается проезд двух колясок одновременно, " +
    "ширина коридора должна быть не менее 1,8 м.",
};
export const MGN_EVAC_PATH_SOURCE: NormAuditSource = {
  document: MGN_DOCUMENT,
  clause: "п. 4.2.3",
  quote:
    "Ширина (в свету) участков эвакуационных путей, используемых маломобильными " +
    "группами населения, должна быть не менее: проемов, дверей и проходов внутри " +
    "помещений – 0,9 м; переходных лоджий и балконов, межквартирных коридоров " +
    "(при открывании дверей внутрь) – 1,5 м; коридоров, пандусов, используемых " +
    "для эвакуации – 1,8 м.",
};

/** Двери доступных путей — 0,9 м. */
export const MGN_DOOR_WIDTH_MM = 900;
export const MGN_DOOR_SOURCE: NormAuditSource = {
  document: MGN_DOCUMENT,
  clause: "п. 4.3.2.14",
  quote:
    "Ширина двери в помещении должна быть не менее 0,9 м. Размеры пространств " +
    "перед дверью для разъезда на инвалидных колясках указаны в приложении Ж.",
};

/** Габариты санитарно-гигиенических помещений жилых зданий (доступных). */
export const MGN_WC_SOURCE: NormAuditSource = {
  document: MGN_DOCUMENT,
  clause: "п. 4.3.3.14",
  quote:
    "Размеры в плане санитарно-гигиенических помещений для индивидуального " +
    "пользования в жилых зданиях должны быть не менее, м: ванной комнаты или " +
    "совмещенного санитарного узла 2,2 × 2,2; туалетной с умывальником, " +
    "рукомойником 1,6 × 2,2; туалетной без умывальника 1,2 × 1,6.",
};

/** Пандусы: основной предел 5%; 8% только для явно отмеченного исключения. */
export const MGN_RAMP_SLOPE_SOURCE: NormAuditSource = {
  document: MGN_DOCUMENT,
  clause: "п. 4.3.2.30",
  quote:
    "Продольный уклон пандуса не должен превышать 5 %, (1:20). В исключительных " +
    "случаях в затесненных местах максимальная высота подъема (марша) не должна " +
    "превышать 0,8 м при уклоне не более 8% (1:12).",
};

/** Зоны маневрирования перед дверью. */
export const MGN_MANEUVERING_SOURCE: NormAuditSource = {
  document: MGN_DOCUMENT,
  clause: "п. 4.3.2.13",
  quote:
    "Глубина пространства для маневрирования кресло-коляски перед дверью при " +
    "открывании «от себя» должна быть не менее 1,2 м, а при открывании «к себе» - " +
    "не менее 1,5 м при ширине не менее 1,5 м.",
};

const CORRIDOR_KEYWORDS: readonly string[] = ["коридор", "corridor", "дәліз", "дализ"];
const EVAC_KEYWORDS: readonly string[] = ["эвак", "evac"];
const SANITARY_KEYWORDS: readonly string[] = [
  "санузел",
  "сан.узел",
  "сан. узел",
  "с/у",
  "туалет",
  "уборн",
  "ванн",
  "душев",
];
const BATH_KEYWORDS: readonly string[] = ["ванн", "санузел", "сан.узел", "сан. узел", "с/у", "совмещ"];
const ACCESSIBLE_KEYWORDS: readonly string[] = [
  "мгн",
  "доступ",
  "универс",
  "инвалид",
  "мүгедек",
  "коляск",
];

function textOf(name?: string, number?: string): string {
  return `${name ?? ""} ${number ?? ""}`.toLowerCase();
}

export function isCorridorRoom(name?: string, number?: string): boolean {
  const text = textOf(name, number);
  return CORRIDOR_KEYWORDS.some((keyword) => text.includes(keyword));
}

export function isEvacuationCorridor(name?: string, number?: string): boolean {
  const text = textOf(name, number);
  return (
    isCorridorRoom(name, number) &&
    EVAC_KEYWORDS.some((keyword) => text.includes(keyword))
  );
}

export function isSanitaryRoom(name?: string, number?: string): boolean {
  const text = textOf(name, number);
  return SANITARY_KEYWORDS.some((keyword) => text.includes(keyword));
}

/**
 * Only rooms explicitly marked accessible (МГН/доступный/универсальный/…) are
 * held to accessible-WC dimensions — обычные квартирные санузлы к ним не
 * относятся и не должны давать ложные нарушения.
 */
export function isAccessibleSanitaryRoom(name?: string, number?: string): boolean {
  const text = textOf(name, number);
  return (
    isSanitaryRoom(name, number) &&
    ACCESSIBLE_KEYWORDS.some((keyword) => text.includes(keyword))
  );
}

/** Требуемые габариты доступного санузла по 4.3.3.14 (мм, меньшая × большая сторона). */
export function requiredAccessibleWcDimsMm(name?: string): [number, number] {
  const text = (name ?? "").toLowerCase();
  const isBath = BATH_KEYWORDS.some((keyword) => text.includes(keyword));
  // Ванная / совмещённый санузел — 2,2 × 2,2; туалетная с умывальником — 1,6 × 2,2.
  return isBath ? [2200, 2200] : [1600, 2200];
}

export interface AccessibilityRoomInput {
  id: number;
  uniqueId?: string;
  name?: string;
  number?: string;
  level?: string;
  widthMm?: number | null;
  depthMm?: number | null;
}

export type AccessibilityStatus = "violation" | "nearLimit" | "compliant";

export interface ClassifiedAccessibilityRoom {
  id: number;
  uniqueId?: string;
  name: string;
  level: string;
  status: AccessibilityStatus;
  widthMm: number;
  depthMm: number;
  /** Limiting actual dimension compared against the norm. */
  actualMm: number;
  requiredMm: number;
  deviationMm: number;
  note?: string;
}

export interface AccessibilityRoomClassification {
  /** Разворот 1,5 м: тамбуры + доступные санузлы (min сторона). */
  turning: ClassifiedAccessibilityRoom[];
  /** Ширина коридоров 1,5 м (эвакуационных — 1,8 м). */
  corridors: ClassifiedAccessibilityRoom[];
  /** Габариты доступных санузлов по 4.3.3.14. */
  wc: ClassifiedAccessibilityRoom[];
  tamboursFound: number;
  corridorsFound: number;
  accessibleWcFound: number;
  missingGeometry: number;
}

export interface ClassifyAccessibilityOptions {
  nearLimitToleranceMm?: number;
}

function readGeometry(
  room: AccessibilityRoomInput
): { widthMm: number; depthMm: number } | null {
  const width = room.widthMm;
  const depth = room.depthMm;
  if (
    width == null ||
    depth == null ||
    !Number.isFinite(width) ||
    !Number.isFinite(depth) ||
    width <= 0 ||
    depth <= 0
  ) {
    return null;
  }
  return { widthMm: width, depthMm: depth };
}

function displayNameOf(room: AccessibilityRoomInput): string {
  return (
    (room.name && room.name.trim()) ||
    (room.number && room.number.trim()) ||
    `помещение ${room.id}`
  );
}

function statusFor(
  deviationMm: number,
  toleranceMm: number
): AccessibilityStatus {
  // Revit feet→mm conversion can leave sub-micrometre noise at an exact limit.
  if (deviationMm <= 0.01) return "compliant";
  if (deviationMm <= toleranceMm) return "nearLimit";
  return "violation";
}

/**
 * Classify accessibility geometry for rooms. Pure — golden tests run without Revit.
 */
export function classifyAccessibilityRooms(
  rooms: AccessibilityRoomInput[],
  options: ClassifyAccessibilityOptions = {}
): AccessibilityRoomClassification {
  const tolerance = options.nearLimitToleranceMm ?? 50;

  const turning: ClassifiedAccessibilityRoom[] = [];
  const corridors: ClassifiedAccessibilityRoom[] = [];
  const wc: ClassifiedAccessibilityRoom[] = [];

  let tamboursFound = 0;
  let corridorsFound = 0;
  let accessibleWcFound = 0;
  let missingGeometry = 0;

  for (const room of rooms) {
    const isTambour = isTambourRoom(room.name, room.number);
    const isCorridor = isCorridorRoom(room.name, room.number);
    const isAccessibleWc = isAccessibleSanitaryRoom(room.name, room.number);

    if (!isTambour && !isCorridor && !isAccessibleWc) continue;

    const geometry = readGeometry(room);
    if (!geometry) {
      missingGeometry += 1;
      continue;
    }

    const base = {
      id: room.id,
      uniqueId: room.uniqueId,
      name: displayNameOf(room),
      level: room.level ?? "",
      widthMm: geometry.widthMm,
      depthMm: geometry.depthMm,
    };
    const minSideMm = Math.min(geometry.widthMm, geometry.depthMm);

    // Разворот 1,5 м — тамбуры и доступные санузлы (коридоры покрыты проверкой ширины).
    if (isTambour || isAccessibleWc) {
      if (isTambour) tamboursFound += 1;
      const deviationMm = MGN_TURNING_DIAMETER_MM - minSideMm;
      turning.push({
        ...base,
        status: statusFor(deviationMm, tolerance),
        actualMm: minSideMm,
        requiredMm: MGN_TURNING_DIAMETER_MM,
        deviationMm: deviationMm > 0 ? deviationMm : 0,
        note: `min сторона ${Math.round(minSideMm)} мм (зона разворота ⌀ ${MGN_TURNING_DIAMETER_MM} мм)`,
      });
    }

    if (isCorridor) {
      corridorsFound += 1;
      const requiredMm = isEvacuationCorridor(room.name, room.number)
        ? MGN_EVAC_CORRIDOR_WIDTH_MM
        : MGN_CORRIDOR_WIDTH_MM;
      const deviationMm = requiredMm - minSideMm;
      corridors.push({
        ...base,
        status: statusFor(deviationMm, tolerance),
        actualMm: minSideMm,
        requiredMm,
        deviationMm: deviationMm > 0 ? deviationMm : 0,
        note: `ширина ${Math.round(minSideMm)} мм (норма ≥ ${requiredMm} мм)`,
      });
    }

    if (isAccessibleWc) {
      accessibleWcFound += 1;
      const [requiredMinMm, requiredMaxMm] = requiredAccessibleWcDimsMm(room.name);
      const actualMinMm = minSideMm;
      const actualMaxMm = Math.max(geometry.widthMm, geometry.depthMm);
      // Orientation-free: меньшая сторона против меньшего норматива, большая — против большего.
      const deviationMm = Math.max(
        requiredMinMm - actualMinMm,
        requiredMaxMm - actualMaxMm
      );
      wc.push({
        ...base,
        status: statusFor(deviationMm, tolerance),
        actualMm: actualMinMm,
        requiredMm: requiredMinMm,
        deviationMm: deviationMm > 0 ? deviationMm : 0,
        note:
          `габарит ${Math.round(actualMinMm)} × ${Math.round(actualMaxMm)} мм ` +
          `(норма ≥ ${requiredMinMm} × ${requiredMaxMm} мм)`,
      });
    }
  }

  return {
    turning,
    corridors,
    wc,
    tamboursFound,
    corridorsFound,
    accessibleWcFound,
    missingGeometry,
  };
}

export const MGN_RAMP_MAX_SLOPE_PERCENT = 5;
export const MGN_RAMP_EXCEPTION_MAX_SLOPE_PERCENT = 8;
export const MGN_RAMP_EXCEPTION_MAX_RISE_MM = 800;
export const MGN_MANEUVERING_WIDTH_MM = 1500;
export const MGN_MANEUVERING_PULL_DEPTH_MM = 1500;
export const MGN_MANEUVERING_PUSH_DEPTH_MM = 1200;

export interface AccessibilityRampInput {
  id: number;
  uniqueId?: string;
  name?: string;
  level?: string;
  slopePercent?: number | null;
  slopeSource?: string;
  riseMm?: number | null;
  isExceptionAllowed?: boolean;
}

export interface AccessibilityDoorManeuveringInput {
  id: number;
  uniqueId?: string;
  family?: string;
  type?: string;
  level?: string;
  isOnEgressPath?: boolean;
  maneuveringDepthMm?: number | null;
  maneuveringWidthMm?: number | null;
  maneuveringRoom?: string;
  maneuveringRequiredDepthMm?: number | null;
  maneuveringApproach?: string;
}

export interface ClassifiedAccessibilityRamp {
  id: number;
  uniqueId?: string;
  name: string;
  level: string;
  status: AccessibilityStatus;
  slopePercent: number;
  requiredMaxPercent: number;
  deviationPercent: number;
  slopeSource: string;
  riseMm?: number;
  exceptionApplied: boolean;
}

export interface ClassifiedDoorManeuvering {
  id: number;
  uniqueId?: string;
  name: string;
  level: string;
  status: AccessibilityStatus;
  actualDepthMm: number;
  actualWidthMm: number;
  requiredDepthMm: number;
  requiredWidthMm: number;
  deviationMm: number;
  roomName: string;
  approach: string;
}

export function classifyAccessibilityRamps(
  ramps: AccessibilityRampInput[],
  nearLimitTolerancePercent = 0.25
): {
  findings: ClassifiedAccessibilityRamp[];
  missingGeometry: number;
} {
  const findings: ClassifiedAccessibilityRamp[] = [];
  let missingGeometry = 0;
  for (const ramp of ramps) {
    const slope = ramp.slopePercent;
    if (slope == null || !Number.isFinite(slope) || slope <= 0) {
      missingGeometry += 1;
      continue;
    }
    const exceptionApplied =
      Boolean(ramp.isExceptionAllowed) &&
      ramp.riseMm != null &&
      ramp.riseMm <= MGN_RAMP_EXCEPTION_MAX_RISE_MM;
    const requiredMaxPercent = exceptionApplied
      ? MGN_RAMP_EXCEPTION_MAX_SLOPE_PERCENT
      : MGN_RAMP_MAX_SLOPE_PERCENT;
    const excess = slope - requiredMaxPercent;
    const status: AccessibilityStatus =
      excess <= 0.0001
        ? "compliant"
        : excess <= nearLimitTolerancePercent
          ? "nearLimit"
          : "violation";
    findings.push({
      id: ramp.id,
      uniqueId: ramp.uniqueId,
      name: ramp.name?.trim() || `пандус ${ramp.id}`,
      level: ramp.level ?? "",
      status,
      slopePercent: slope,
      requiredMaxPercent,
      deviationPercent: excess > 0 ? excess : 0,
      slopeSource: ramp.slopeSource ?? "",
      ...(ramp.riseMm != null ? { riseMm: ramp.riseMm } : {}),
      exceptionApplied,
    });
  }
  return { findings, missingGeometry };
}

export function classifyDoorManeuvering(
  doors: AccessibilityDoorManeuveringInput[],
  nearLimitToleranceMm = 50
): {
  findings: ClassifiedDoorManeuvering[];
  unmeasured: AccessibilityDoorManeuveringInput[];
  missingGeometry: number;
  nonEgressSkipped: number;
} {
  const findings: ClassifiedDoorManeuvering[] = [];
  const unmeasured: AccessibilityDoorManeuveringInput[] = [];
  let missingGeometry = 0;
  let nonEgressSkipped = 0;
  for (const door of doors) {
    if (!door.isOnEgressPath) {
      nonEgressSkipped += 1;
      continue;
    }
    const depth = door.maneuveringDepthMm;
    const width = door.maneuveringWidthMm;
    if (
      depth == null ||
      width == null ||
      !Number.isFinite(depth) ||
      !Number.isFinite(width) ||
      depth <= 0 ||
      width <= 0
    ) {
      missingGeometry += 1;
      unmeasured.push(door);
      continue;
    }
    const requiredDepth =
      door.maneuveringRequiredDepthMm ?? MGN_MANEUVERING_PULL_DEPTH_MM;
    const deviation = Math.max(
      requiredDepth - depth,
      MGN_MANEUVERING_WIDTH_MM - width
    );
    findings.push({
      id: door.id,
      uniqueId: door.uniqueId,
      name: `${door.family ?? ""} ${door.type ?? ""}`.trim() || `дверь ${door.id}`,
      level: door.level ?? "",
      status: statusFor(deviation, nearLimitToleranceMm),
      actualDepthMm: depth,
      actualWidthMm: width,
      requiredDepthMm: requiredDepth,
      requiredWidthMm: MGN_MANEUVERING_WIDTH_MM,
      deviationMm: deviation > 0 ? deviation : 0,
      roomName: door.maneuveringRoom ?? "",
      approach: door.maneuveringApproach ?? "",
    });
  }
  return { findings, unmeasured, missingGeometry, nonEgressSkipped };
}
