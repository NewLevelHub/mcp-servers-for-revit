/**
 * Attach the maneuvering-space requirement, and a verdict, to raw door data
 * (REV-50).
 *
 * Found on acceptance, 18.08.2026. `get_door_egress_info` returned
 * `maneuveringDepthMm`, `maneuveringWidthMm` and `maneuveringRequiredDepthMm` —
 * a required depth but no required width. Asked about fire doors, the model read
 * those three numbers, borrowed the depth requirement for the width, and reported
 * a 1375 mm width as failing a "1500 mm width" rule it had inferred.
 *
 * It happened to be right — СП РК 3.06-101 п. 4.3.2.12 does require 1.5 m of
 * width — but it was right by accident, and on the very next door (1450 mm, the
 * same violation) it said everything was fine. A number the model has to judge
 * for itself gets judged inconsistently.
 *
 * So the requirement travels with the measurement, and the verdict is computed
 * here rather than inferred there. The thresholds are the ones
 * `classifyDoorManeuvering` already applies in `check_accessibility`, imported
 * rather than copied so the raw reader and the audit can never disagree.
 */
import {
  MGN_MANEUVERING_PULL_DEPTH_MM,
  MGN_MANEUVERING_WIDTH_MM,
} from "./accessibility.js";

/** Clause the thresholds come from, quoted so the model can cite it. */
export const MANEUVERING_CLAUSE =
  "СП РК 3.06-101 п. 4.3.2.12: глубина пространства для маневрирования кресло-коляски " +
  "перед дверью при открывании «от себя» — не менее 1,2 м, при открывании «к себе» — " +
  "не менее 1,5 м при ширине не менее 1,5 м";

export type ManeuveringVerdict = "ok" | "near_limit" | "violation" | "not_measured";

/** Within this much of a limit, report it as borderline rather than passing. */
export const NEAR_LIMIT_TOLERANCE_MM = 50;

interface DoorRow {
  maneuveringDepthMm?: number | null;
  maneuveringWidthMm?: number | null;
  maneuveringRequiredDepthMm?: number | null;
  isOnEgressPath?: boolean;
  [key: string]: unknown;
}

function positive(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) && value > 0
    ? value
    : null;
}

export interface AnnotatedManeuvering {
  maneuveringRequiredWidthMm: number;
  maneuveringVerdict: ManeuveringVerdict;
  /** Shortfall against the tighter of the two limits, mm. 0 when it passes. */
  maneuveringDeviationMm: number;
  maneuveringNote: string;
}

/**
 * Verdict for one door. Both limits are checked and the worse one decides — a
 * door with ample depth and a narrow approach is still unusable in a wheelchair.
 */
export function annotateDoorManeuvering(door: DoorRow): AnnotatedManeuvering {
  const depth = positive(door.maneuveringDepthMm);
  const width = positive(door.maneuveringWidthMm);
  const requiredDepth =
    positive(door.maneuveringRequiredDepthMm) ?? MGN_MANEUVERING_PULL_DEPTH_MM;
  const requiredWidth = MGN_MANEUVERING_WIDTH_MM;

  if (depth === null || width === null) {
    return {
      maneuveringRequiredWidthMm: requiredWidth,
      maneuveringVerdict: "not_measured",
      maneuveringDeviationMm: 0,
      maneuveringNote:
        "Зона манёвра не измерена — дверь не граничит с помещением или помещение не замкнуто. " +
        "Проверить вручную.",
    };
  }

  const shortfall = Math.max(requiredDepth - depth, requiredWidth - width);

  if (shortfall > 0) {
    const parts: string[] = [];
    if (depth < requiredDepth) parts.push(`глубина ${Math.round(depth)} < ${requiredDepth}`);
    if (width < requiredWidth) parts.push(`ширина ${Math.round(width)} < ${requiredWidth}`);
    return {
      maneuveringRequiredWidthMm: requiredWidth,
      maneuveringVerdict: "violation",
      maneuveringDeviationMm: Math.round(shortfall),
      maneuveringNote: `Не проходит по МГН: ${parts.join(", ")} мм. ${MANEUVERING_CLAUSE}.`,
    };
  }

  const margin = Math.min(depth - requiredDepth, width - requiredWidth);
  if (margin <= NEAR_LIMIT_TOLERANCE_MM) {
    return {
      maneuveringRequiredWidthMm: requiredWidth,
      maneuveringVerdict: "near_limit",
      maneuveringDeviationMm: 0,
      maneuveringNote:
        `Впритык: запас ${Math.round(margin)} мм. Любая правка планировки выведет за норму.`,
    };
  }

  return {
    maneuveringRequiredWidthMm: requiredWidth,
    maneuveringVerdict: "ok",
    maneuveringDeviationMm: 0,
    maneuveringNote: "",
  };
}

export interface ManeuveringSummary {
  checked: number;
  violations: number;
  nearLimit: number;
  notMeasured: number;
  clause: string;
  note: string;
}

/**
 * Annotate every door and summarise. The summary exists so a model that reads
 * only the top of the payload still gets the verdict — the failure this fixes was
 * exactly a model drawing its own conclusion from rows it read one at a time.
 */
export function annotateDoorEgressResponse(response: unknown): unknown {
  if (!response || typeof response !== "object" || Array.isArray(response)) {
    return response;
  }

  const bag = response as Record<string, unknown>;
  const key = Array.isArray(bag.doors)
    ? "doors"
    : Array.isArray(bag.Doors)
      ? "Doors"
      : null;
  if (!key) return response;

  const summary: ManeuveringSummary = {
    checked: 0,
    violations: 0,
    nearLimit: 0,
    notMeasured: 0,
    clause: MANEUVERING_CLAUSE,
    note: "",
  };

  const doors = (bag[key] as DoorRow[]).map((door) => {
    if (!door || typeof door !== "object") return door;
    const annotated = annotateDoorManeuvering(door);
    summary.checked += 1;
    if (annotated.maneuveringVerdict === "violation") summary.violations += 1;
    else if (annotated.maneuveringVerdict === "near_limit") summary.nearLimit += 1;
    else if (annotated.maneuveringVerdict === "not_measured") summary.notMeasured += 1;
    return { ...door, ...annotated };
  });

  summary.note =
    summary.violations > 0
      ? `Не проходят по зоне манёвра МГН: ${summary.violations} из ${summary.checked}. ` +
        "Смотри maneuveringVerdict у каждой двери — не сравнивай размеры сам."
      : `Все измеренные двери проходят по зоне манёвра МГН (${summary.checked}).`;

  return { ...bag, [key]: doors, maneuveringSummary: summary };
}
