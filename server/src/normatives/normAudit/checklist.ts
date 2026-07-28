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
    | "window_sill_height"
    | "opening_height"
    | "stair_width"
    | "stair_riser_tread"
    | "ramp_slope_width"
    | "railing_height"
  >;
  title: string;
  /** Substrings matched against user topics (lowercase). Empty topics → all (except optInOnly). */
  topicHints: string[];
  /**
   * If true, checker is skipped on full audit (no topics).
   * Used for МГН / accessibility — only when user asks topics=["мгн"] etc.
   */
  optInOnly?: boolean;
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
    optInOnly: true,
    topicHints: [
      "мгн",
      "доступност",
      "инвалид",
      "коляск",
      "кресло",
      "разворот",
      "accessibility",
      "мүгедек",
    ],
  },
  {
    checkType: "mgn_door_width",
    title: "МГН: ширина дверей доступных путей (0,9 м)",
    optInOnly: true,
    topicHints: [
      "мгн",
      "доступност",
      "инвалид",
      "коляск",
      "accessibility",
      "мүгедек",
      "зона маневрирован",
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
  {
    checkType: "window_sill_height",
    title: "Высота подоконника",
    topicHints: [
      "подокон",
      "sill",
      "высота подоконника",
      "оконный проём",
      "оконный проем",
      "окно",
      "терезе",
    ],
  },
  {
    checkType: "opening_height",
    title: "Высота дверного / оконного проёма",
    topicHints: [
      "высота проём",
      "высота проем",
      "высота двери",
      "высота выхода",
      "opening height",
      "эвак. выход",
      "в свету",
      "дверн",
    ],
  },
  {
    checkType: "stair_width",
    title: "Ширина лестничного марша",
    topicHints: [
      "лестниц",
      "марш",
      "ширина марша",
      "stair",
      "баспалдақ",
    ],
  },
  {
    checkType: "stair_riser_tread",
    title: "Подступенок / проступь",
    topicHints: [
      "подступенок",
      "проступь",
      "ступен",
      "riser",
      "tread",
      "высота ступени",
    ],
  },
  {
    checkType: "ramp_slope_width",
    title: "Уклон и ширина пандуса",
    topicHints: [
      "пандус",
      "уклон",
      "еңіс",
      "ramp",
      "доступност",
    ],
  },
  {
    checkType: "railing_height",
    title: "Высота ограждения",
    topicHints: [
      "огражд",
      "перил",
      "railing",
      "қоршау",
      "высота ограждения",
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
  {
    checkType: "mgn_door_maneuvering",
    reason:
      "Проверки МГН / доступности (СП РК 3.06-101-2012*) по умолчанию выключены для обычного жилья. " +
      "Запустите с topics=[\"мгн\"] или «проверь доступность МГН».",
    topics: ["мгн", "доступност", "accessibility"],
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
  const hasTopics = Boolean(topics && topics.length > 0);
  return PHASE1_CHECKERS.filter((checker) => {
    if (checker.optInOnly && !hasTopics) return false;
    return topicMatchesHints(topics, checker.topicHints);
  });
}

export function selectSkippedRules(
  topics?: string[]
): NormAuditSkippedRule[] {
  // Always surface Phase-2 gaps when running a full audit (no topic filter).
  // With a topic filter, only show skipped rules that match the request —
  // so «проверь двери» honestly says door clear width is not implemented.
  // МГН opt-in skip is shown on full audit, but not when topics already ask for МГН.
  if (!topics || topics.length === 0) {
    return [...PHASE2_SKIPPED];
  }
  return PHASE2_SKIPPED.filter((rule) => {
    if (rule.checkType === "mgn_door_maneuvering") {
      // User explicitly asked for МГН — checker runs, do not list as skipped.
      if (topicMatchesHints(topics, rule.topics)) return false;
    }
    return topicMatchesHints(topics, rule.topics);
  });
}
