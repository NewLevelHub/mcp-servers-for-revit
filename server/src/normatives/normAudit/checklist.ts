import type { NormAuditCheckType, NormAuditSkippedRule } from "./types.js";

export interface NormAuditCheckerDef {
  checkType: Extract<
    NormAuditCheckType,
    | "evacuation_width"
    | "room_depth"
    | "min_dimensions"
    | "fire_doors"
    | "door_clear_width"
    | "tambour_size_min"
    | "mgn_room_geometry"
    | "mgn_door_width"
    | "room_area_min"
    | "room_height_min"
    | "storey_height"
  >;
  title: string;
  /** Substrings matched against user topics (lowercase). Empty topics → all. */
  topicHints: string[];
}

/** Phase 1 checkers wrapped by run_norm_audit. */
export const PHASE1_CHECKERS: readonly NormAuditCheckerDef[] = [
  {
    checkType: "evacuation_width",
    title: "Ширина эвак. коридоров / тамбуров",
    topicHints: [
      "эвак",
      "коридор",
      "тамбур",
      "ширина коридор",
      "проход",
      "дәліз",
      "corridor",
    ],
  },
  {
    checkType: "room_depth",
    title: "Глубина помещений",
    topicHints: ["глубин", "depth", "помещен"],
  },
  {
    checkType: "min_dimensions",
    title: "Лоджии / балконы / простенки",
    topicHints: [
      "лоджи",
      "балкон",
      "простенок",
      "простенк",
      "терраса",
      "loggia",
      "balcony",
    ],
  },
  {
    checkType: "fire_doors",
    title: "Противопожарные двери",
    topicHints: [
      "противопож",
      "пд",
      "fire",
      "ei ",
      "ei30",
      "ei 30",
      "двер",
    ],
  },
  {
    checkType: "door_clear_width",
    title: "Ширина двери / проёма (номинал)",
    topicHints: [
      "двер",
      "door",
      "ширина двери",
      "дверной проём",
      "дверной проем",
      "проём",
      "проем",
      "есік",
      "ойық",
      "эвак. выход",
      "выход",
    ],
  },
  {
    checkType: "tambour_size_min",
    title: "Габариты входного тамбура",
    topicHints: [
      "тамбур",
      "tambour",
      "vestibule",
      "входной тамбур",
      "тамбұр",
      "кіреберіс",
      "1.65",
      "1,65",
    ],
  },
  {
    checkType: "mgn_room_geometry",
    title: "МГН: разворот кресла-коляски, коридоры, санузлы",
    topicHints: [
      "мгн",
      "доступ",
      "инвалид",
      "коляск",
      "кресло",
      "разворот",
      "accessibility",
      "мүгедек",
      "1500",
      "1,5 м",
    ],
  },
  {
    checkType: "mgn_door_width",
    title: "МГН: ширина дверей доступных путей (0,9 м)",
    topicHints: [
      "мгн",
      "доступ",
      "инвалид",
      "коляск",
      "accessibility",
      "мүгедек",
      "двер",
      "0,9",
    ],
  },
  {
    checkType: "room_area_min",
    title: "Мин. площадь помещений",
    topicHints: [
      "площад",
      "area",
      "жилая",
      "кухня",
      "санузел",
      "спальня",
      "комнат",
    ],
  },
  {
    checkType: "room_height_min",
    title: "Мин. высота помещений",
    topicHints: [
      "высота помещ",
      "высота потолка",
      "room height",
      "unbounded",
      "биіктік",
    ],
  },
  {
    checkType: "storey_height",
    title: "Высота этажа",
    topicHints: [
      "высота этажа",
      "storey",
      "этажность",
      "қабат биіктігі",
      "перепад уровней",
    ],
  },
] as const;

/**
 * Known measurable rules without a checker yet — always reported as skipped.
 */
export const PHASE2_SKIPPED: readonly NormAuditSkippedRule[] = [
  {
    checkType: "egress_opening_width",
    reason:
      "Checker ширины эвакуационного выхода / проёма в стене ещё не реализован (Phase 2).",
    topics: ["эвак. выход", "ширина проёма", "выход"],
  },
  {
    checkType: "passage_width",
    reason:
      "Отдельный checker мин. ширины прохода (если не покрыто коридором) ещё не реализован (Phase 2).",
    topics: ["ширина прохода"],
  },
] as const;

export const AUDIT_SCOPE_NOTE =
  "Проверяем только измеримые геометрические требования, для которых есть checker. " +
  "Это не полный проход ГОСТ/СП/СН РК и не замена ГИП / экспертизы.";

function normalizeTopic(topic: string): string {
  return topic.trim().toLowerCase();
}

/** True if any user topic matches any hint (substring either way). */
export function topicMatchesHints(
  topics: string[] | undefined,
  hints: readonly string[]
): boolean {
  if (!topics || topics.length === 0) return true;
  const normalized = topics.map(normalizeTopic).filter(Boolean);
  if (normalized.length === 0) return true;

  return normalized.some((topic) =>
    hints.some(
      (hint) => topic.includes(hint) || hint.includes(topic) || topic === hint
    )
  );
}

export function selectPhase1Checkers(
  topics?: string[]
): NormAuditCheckerDef[] {
  return PHASE1_CHECKERS.filter((checker) =>
    topicMatchesHints(topics, checker.topicHints)
  );
}

export function selectSkippedRules(
  topics?: string[]
): NormAuditSkippedRule[] {
  // Always surface Phase-2 gaps when running a full audit (no topic filter).
  // With a topic filter, only show skipped rules that match the request —
  // so «проверь двери» honestly says door clear width is not implemented.
  if (!topics || topics.length === 0) {
    return [...PHASE2_SKIPPED];
  }
  return PHASE2_SKIPPED.filter((rule) =>
    topicMatchesHints(topics, rule.topics)
  );
}
