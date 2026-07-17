import type { Database } from "better-sqlite3";
import { queryNormRules, type StoredNormRule } from "../rulesStore.js";
import type { NormAuditSource } from "./types.js";
import { toAuditSource } from "./types.js";

export interface ResolvedWindowSillLimit {
  minSillHeightMm: number;
  source: NormAuditSource;
  rule: StoredNormRule;
}

const SILL_TOPICS = [
  "высота подоконника",
  "подоконник от пола",
  "высота подоконника окна",
  "низ оконного проёма от пола",
  "window sill height",
] as const;

/** Plausible residential sill mins are ~0.5–1.2 m. */
const MIN_PLAUSIBLE_SILL_MM = 400;
const MAX_PLAUSIBLE_SILL_MM = 1400;

function ruleBlob(rule: StoredNormRule): string {
  return `${rule.object} ${rule.source.quote} ${rule.applicability?.raw ?? ""}`.toLowerCase();
}

function heightMinMmOf(rule: StoredNormRule): number | undefined {
  const fromNormalized = rule.normalized?.min ?? rule.normalized?.exact;
  if (fromNormalized != null) return fromNormalized;
  if (
    (rule.type === "min_value" || rule.type === "exact_value") &&
    typeof rule.value === "number"
  ) {
    if (rule.unit === "mm") return rule.value;
    if (rule.unit === "m") return rule.value * 1000;
  }
  return undefined;
}

function mentionsSill(blob: string): boolean {
  return (
    blob.includes("подокон") ||
    blob.includes("sill") ||
    blob.includes("низ окон") ||
    blob.includes("низа окон") ||
    (blob.includes("окон") && blob.includes("от пола"))
  );
}

function isExcludedSillRule(rule: StoredNormRule): boolean {
  const blob = ruleBlob(rule);
  if (blob.includes("мусор")) return true;
  if (blob.includes("шибер")) return true;
  if (blob.includes("кабел")) return true;
  if (blob.includes("стойк")) return true;
  if (blob.includes("поручн")) return true;
  if (blob.includes("ограждени") && !mentionsSill(blob)) return true;
  return false;
}

/**
 * Score a rule as a window-sill minimum height. Returns <=0 to disqualify.
 */
export function scoreWindowSillRule(rule: StoredNormRule): number {
  const minMm = heightMinMmOf(rule);
  if (minMm == null) return -1000;
  if (minMm < MIN_PLAUSIBLE_SILL_MM || minMm > MAX_PLAUSIBLE_SILL_MM) {
    return -1000;
  }
  if (isExcludedSillRule(rule)) return -1000;
  if (rule.type === "max_value") return -1000;

  const blob = ruleBlob(rule);
  if (!mentionsSill(blob)) return -1000;

  let score = 40;
  if (blob.includes("подокон")) score += 30;
  if (blob.includes("от пола") || blob.includes("еден")) score += 15;
  if (/3\.02-101|3\.06-101|3\.06-01/.test(rule.source.document)) score += 20;
  if (rule.type === "min_value") score += 10;
  if (minMm >= 700 && minMm <= 1000) score += 10;
  if (rule.source.clause) score += 5;

  return score;
}

export function pickBestWindowSillRule(
  rules: StoredNormRule[]
): StoredNormRule | null {
  const ranked = rules
    .map((rule) => ({ rule, score: scoreWindowSillRule(rule) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score);

  return ranked[0]?.rule ?? null;
}

/**
 * Resolve a minimum window sill height from the SQLite norm library.
 * Returns null (→ check skipped) when no plausible sill rule is found —
 * never invents a number.
 */
export function resolveWindowSillLimitFromLibrary(
  db: Database
): ResolvedWindowSillLimit | null {
  const seen = new Map<string, StoredNormRule>();
  for (const topic of SILL_TOPICS) {
    for (const rule of queryNormRules(db, { topic, limit: 12 })) {
      if (!seen.has(rule.id)) seen.set(rule.id, rule);
    }
  }

  const rule = pickBestWindowSillRule([...seen.values()]);
  if (!rule) return null;

  const minSillHeightMm = heightMinMmOf(rule);
  if (minSillHeightMm == null) return null;

  return {
    minSillHeightMm,
    source: toAuditSource(rule.source),
    rule,
  };
}
