import type { Database } from "better-sqlite3";
import { queryNormRules, type StoredNormRule } from "../rulesStore.js";
import type { NormAuditSource } from "./types.js";
import { toAuditSource } from "./types.js";

export interface ResolvedTambourSizeLimit {
  minSideMm: number;
  source: NormAuditSource;
  rule: StoredNormRule;
}

const TAMBOUR_SIZE_TOPICS = [
  "тамбур размер 1.65 входной",
  "тамбур кемінде 1,65 м",
  "размер входного тамбура",
] as const;

const MIN_PLAUSIBLE_TAMBOUR_SIDE_MM = 1200;
const MAX_PLAUSIBLE_TAMBOUR_SIDE_MM = 2500;

function ruleBlob(rule: StoredNormRule): string {
  return `${rule.object} ${rule.source.quote} ${rule.applicability?.raw ?? ""}`.toLowerCase();
}

function sideMinMmOf(rule: StoredNormRule): number | undefined {
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

function isExcludedTambourRule(rule: StoredNormRule): boolean {
  const blob = ruleBlob(rule);
  if (blob.includes("контейнер") || blob.includes("қоқыс")) return true;
  if (blob.includes("мусор")) return true;
  if (blob.includes("коридор") && !blob.includes("тамбур")) return true;
  return false;
}

/** Score a rule as minimum tambour side length. Returns <=0 to disqualify. */
export function scoreTambourSizeRule(rule: StoredNormRule): number {
  const sideMm = sideMinMmOf(rule);
  if (sideMm == null) return -1000;
  if (sideMm < MIN_PLAUSIBLE_TAMBOUR_SIDE_MM || sideMm > MAX_PLAUSIBLE_TAMBOUR_SIDE_MM) {
    return -1000;
  }
  if (isExcludedTambourRule(rule)) return -1000;

  const blob = ruleBlob(rule);
  if (!blob.includes("тамбур") && !blob.includes("tambour") && !blob.includes("тамбұр")) {
    return -1000;
  }

  let score = 40;
  if (/3\.02-101/.test(rule.source.document)) score += 25;
  if (blob.includes("кірер") || blob.includes("вход") || blob.includes("негізгі")) {
    score += 15;
  }
  if (blob.includes("×") || blob.includes("x") || blob.includes("х")) score += 10;
  if (sideMm >= 1600 && sideMm <= 1700) score += 15;
  if (rule.source.clause.includes("4.4.10")) score += 20;
  if (rule.type === "min_value") score += 10;

  return score;
}

export function pickBestTambourSizeRule(
  rules: StoredNormRule[]
): StoredNormRule | null {
  const ranked = rules
    .map((rule) => ({ rule, score: scoreTambourSizeRule(rule) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score);

  return ranked[0]?.rule ?? null;
}

/**
 * Resolve minimum tambour side length from the SQLite norm library.
 * Typical value: 1650 mm (1.65 m × 1.65 m per СП РК 3.02-101-2012 п. 4.4.10.6).
 */
export function resolveTambourSizeLimitFromLibrary(
  db: Database
): ResolvedTambourSizeLimit | null {
  const seen = new Map<string, StoredNormRule>();

  for (const topic of TAMBOUR_SIZE_TOPICS) {
    for (const rule of queryNormRules(db, { topic, limit: 12 })) {
      if (!seen.has(rule.id)) seen.set(rule.id, rule);
    }
  }

  const rule = pickBestTambourSizeRule([...seen.values()]);
  if (!rule) return null;

  const minSideMm = sideMinMmOf(rule);
  if (minSideMm == null) return null;

  return {
    minSideMm,
    source: toAuditSource(rule.source),
    rule,
  };
}
