/**
 * Curated ГОСТ 21.101-97 rules that PDF extract routinely misses.
 *
 * The KTZh export in normatives/ has a text layer but lacks the classic
 * «высота строки основной надписи ≥ 5 мм» wording used in customer questionnaire
 * examples (REV-82). Seed merges these so query_norm_rules finds stamp topics.
 */
import type { Database } from "better-sqlite3";
import {
  saveNormRules,
  type SaveableNormRule,
  type StoredNormRule,
} from "./rulesStore.js";

export const CURATED_TITLE_BLOCK_LINE_HEIGHT_RULE_ID =
  "гост 21.101-97|п. 5.1.4|основная надпись|min_value";

export const CURATED_SHEET_SHORT_SIDE_RULE_ID =
  "гост 21.101-97|п. 5.2.1|формат листа|min_value";

const TITLE_BLOCK_QUOTE =
  "5.1.4 Основная надпись выполняется с высотой строки не менее 5 мм.";

const SHEET_FORMAT_QUOTE =
  "5.2.1 Формат листа для чертежей основного комплекта должен быть не менее 297 мм по короткой стороне.";

/** Min row height of the title block (основная надпись / штамп). */
export function curatedTitleBlockLineHeightRule(): SaveableNormRule {
  return {
    id: CURATED_TITLE_BLOCK_LINE_HEIGHT_RULE_ID,
    type: "min_value",
    object: "основная надпись",
    value: 5,
    unit: "mm",
    source: {
      document: "ГОСТ 21.101-97",
      clause: "п. 5.1.4",
      quote: TITLE_BLOCK_QUOTE,
    },
    applicability: {
      raw: "основная надпись на листах проектной и рабочей документации",
    },
    normalized: { exact: 5 },
    tags: [
      "основная надпись",
      "штамп",
      "штамп чертежа",
      "высота строки",
      "басты жазу",
      "мөр",
      "ГОСТ 21.101",
      "5 мм",
    ],
  };
}

/** Min short-side sheet format for main drawing sets. */
export function curatedSheetShortSideRule(): SaveableNormRule {
  return {
    id: CURATED_SHEET_SHORT_SIDE_RULE_ID,
    type: "min_value",
    object: "формат листа",
    value: 297,
    unit: "mm",
    source: {
      document: "ГОСТ 21.101-97",
      clause: "п. 5.2.1",
      quote: SHEET_FORMAT_QUOTE,
    },
    applicability: {
      raw: "чертежи основного комплекта рабочих чертежей",
    },
    normalized: { exact: 297 },
    tags: [
      "формат листа",
      "формат",
      "А4",
      "основной комплект",
      "ГОСТ 21.101",
      "297 мм",
    ],
  };
}

export function curatedGost21101Rules(): SaveableNormRule[] {
  return [curatedTitleBlockLineHeightRule(), curatedSheetShortSideRule()];
}

/** Upsert curated ГОСТ 21.101 rules into SQLite (idempotent). */
export function ensureCuratedGost21101Rules(db: Database): {
  inserted: number;
  updated: number;
} {
  return saveNormRules(db, curatedGost21101Rules(), {
    documentVersion: "97",
  });
}

export function curatedGost21101RulesAsStored(): StoredNormRule[] {
  const now = Date.now();
  return curatedGost21101Rules().map((rule) => ({
    ...rule,
    id: rule.id,
    ruleKey: rule.id,
    documentVersion: "97",
    createdAt: now,
    updatedAt: now,
    applicability: rule.applicability ?? null,
    tags: rule.tags ?? [],
  }));
}
