import type { Database } from "better-sqlite3";
import { queryNormRules, type StoredNormRule } from "../rulesStore.js";
import type { NormAuditSource } from "./types.js";
import { toAuditSource } from "./types.js";

export interface ResolvedOpeningHeightLimit {
  minHeightMm: number;
  source: NormAuditSource;
  rule: StoredNormRule;
}

const OPENING_HEIGHT_TOPICS = [
  "высота эвакуационных выходов в свету",
  "высота эвакуационного выхода",
  "высота дверного проёма эвакуация",
  "высота проёма двери",
] as const;

/** Egress opening height mins are typically 1.8–2.2 m. */
const MIN_PLAUSIBLE_OPENING_HEIGHT_MM = 1700;
const MAX_PLAUSIBLE_OPENING_HEIGHT_MM = 2400;

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

function mentionsEgressExitHeight(blob: string): boolean {
  const hasHeight =
    blob.includes("высот") || blob.includes("биікт") || blob.includes("height");
  const hasExit =
    blob.includes("выход") ||
    blob.includes("шығу") ||
    blob.includes("проём") ||
    blob.includes("проем") ||
    blob.includes("двер");
  const hasEgress =
    blob.includes("эвакуац") ||
    blob.includes("эвак") ||
    blob.includes("в свету") ||
    blob.includes("в свете");
  return hasHeight && hasExit && (hasEgress || blob.includes("дверн"));
}

function isExcludedOpeningHeightRule(rule: StoredNormRule): boolean {
  const blob = ruleBlob(rule);
  if (blob.includes("путей эвакуации") && blob.includes("горизонтальн")) return true;
  if (blob.includes("путей в здании") && !blob.includes("выход")) return true;
  if (blob.includes("мусор")) return true;
  if (blob.includes("порог")) return true;
  if (blob.includes("маркировк")) return true;
  if (blob.includes("ручк")) return true;
  if (blob.includes("замок")) return true;
  if (blob.includes("подокон")) return true;
  return false;
}

/**
 * Score a rule as an egress opening / door height minimum.
 */
export function scoreOpeningHeightRule(rule: StoredNormRule): number {
  const minMm = heightMinMmOf(rule);
  if (minMm == null) return -1000;
  if (
    minMm < MIN_PLAUSIBLE_OPENING_HEIGHT_MM ||
    minMm > MAX_PLAUSIBLE_OPENING_HEIGHT_MM
  ) {
    return -1000;
  }
  if (isExcludedOpeningHeightRule(rule)) return -1000;
  if (rule.type === "max_value") return -1000;

  const blob = ruleBlob(rule);
  if (!mentionsEgressExitHeight(blob)) return -1000;

  let score = 40;
  if (blob.includes("в свету") || blob.includes("в свете")) score += 25;
  if (blob.includes("эвакуац")) score += 20;
  if (blob.includes("выход")) score += 15;
  if (/3\.06-31|3\.02-101|3\.06-101/.test(rule.source.document)) score += 20;
  if (rule.type === "min_value") score += 10;
  if (minMm >= 1850 && minMm <= 2100) score += 15;
  if (minMm === 1900) score += 10;
  if (rule.source.clause) score += 5;

  return score;
}

export function pickBestOpeningHeightRule(
  rules: StoredNormRule[]
): StoredNormRule | null {
  const ranked = rules
    .map((rule) => ({ rule, score: scoreOpeningHeightRule(rule) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score);

  return ranked[0]?.rule ?? null;
}

/**
 * Resolve a minimum egress opening height from the SQLite norm library.
 * Prefer «высота эвакуационных выходов в свету ≥ 1,9 м» (SP RK 3.06-31).
 */
export function resolveOpeningHeightLimitFromLibrary(
  db: Database
): ResolvedOpeningHeightLimit | null {
  const seen = new Map<string, StoredNormRule>();
  for (const topic of OPENING_HEIGHT_TOPICS) {
    for (const rule of queryNormRules(db, { topic, limit: 12 })) {
      if (!seen.has(rule.id)) seen.set(rule.id, rule);
    }
  }

  const rule = pickBestOpeningHeightRule([...seen.values()]);
  if (!rule) return null;

  const minHeightMm = heightMinMmOf(rule);
  if (minHeightMm == null) return null;

  return {
    minHeightMm,
    source: toAuditSource(rule.source),
    rule,
  };
}
