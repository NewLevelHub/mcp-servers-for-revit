import type { Database } from "better-sqlite3";
import {
  queryNormRules,
  type StoredNormRule,
} from "../rulesStore.js";
import type { NormAuditSource } from "./types.js";
import { toAuditSource } from "./types.js";

export interface ResolvedRoomDepthLimit {
  minDepthMm?: number;
  maxDepthMm?: number;
  source: NormAuditSource;
  rule: StoredNormRule;
}

const DEPTH_TOPICS = [
  "глубина помещения",
  "максимальная глубина комнаты",
  "глубина жилой комнаты",
] as const;

/** Plausible band for a room-depth MAXIMUM (mm). 1600 mm etc. is not a depth max. */
const MIN_PLAUSIBLE_DEPTH_MAX_MM = 3000;
const MAX_PLAUSIBLE_DEPTH_MAX_MM = 12000;

function hasNumericNormalized(rule: StoredNormRule): boolean {
  const n = rule.normalized;
  return (
    n != null &&
    (n.min != null || n.max != null || n.exact != null)
  );
}

function ruleBlob(rule: StoredNormRule): string {
  return `${rule.object} ${rule.source.quote} ${rule.applicability?.raw ?? ""}`.toLowerCase();
}

/**
 * Extract a room-depth MAXIMUM from the rule.
 * Room depth is a max constraint («глубина не более X»); a min-only rule
 * (e.g. «ширина передней ≥ 1,6 м») must NOT be treated as a depth limit.
 */
function depthMaxOf(rule: StoredNormRule): number | undefined {
  const fromNormalized = rule.normalized?.max ?? rule.normalized?.exact;
  if (fromNormalized != null) return fromNormalized;
  if (
    (rule.type === "max_value" || rule.type === "exact_value") &&
    typeof rule.value === "number"
  ) {
    return rule.value;
  }
  return undefined;
}

/** Reject kitchen area, loggia width, corridor width mistaken as room depth. */
function isExcludedDepthRule(rule: StoredNormRule): boolean {
  const blob = ruleBlob(rule);
  if (blob.includes("лоджи") || blob.includes("балкон")) return true;
  if (blob.includes("кухн")) return true;
  if (
    (blob.includes("площад") || blob.includes("м²") || blob.includes("м2")) &&
    !blob.includes("глубин")
  ) {
    return true;
  }
  if (blob.includes("коридор") && !blob.includes("глубин")) return true;
  if (blob.includes("двер") && !blob.includes("глубин")) return true;
  return false;
}

/**
 * Score a rule as a room-depth limit. Returns <=0 to disqualify.
 * Requires: the rule OBJECT is about depth (not just prose that mentions
 * «глубиной» of a cabinet), and a plausible numeric MAX depth.
 */
function scoreDepthRule(rule: StoredNormRule): number {
  if (!hasNumericNormalized(rule)) return -1000;
  if (isExcludedDepthRule(rule)) return -1000;

  const object = rule.object.toLowerCase();
  // Subject of the rule must be depth itself — this rejects «ширина передней»
  // rules whose quote merely contains «шкафами глубиной 60 см».
  if (!object.includes("глубин")) return -1000;
  // Furniture / cabinet / niche depth is not room depth.
  if (
    object.includes("шкаф") ||
    object.includes("мебел") ||
    object.includes("ниш")
  ) {
    return -1000;
  }

  // Room depth is a MAXIMUM constraint — a min-only rule cannot bound depth.
  const maxMm = depthMaxOf(rule);
  if (maxMm == null) return -1000;
  if (maxMm < MIN_PLAUSIBLE_DEPTH_MAX_MM || maxMm > MAX_PLAUSIBLE_DEPTH_MAX_MM) {
    return -1000;
  }

  let score = 40;
  if (object.includes("комнат") || object.includes("помещен")) score += 15;
  if (ruleBlob(rule).includes("жил")) score += 8;
  if (rule.type === "max_value") score += 10;
  if (maxMm >= 3000 && maxMm <= 9000) score += 10;

  return score;
}

export function pickBestDepthRule(rules: StoredNormRule[]): StoredNormRule | null {
  const ranked = rules
    .map((rule) => ({ rule, score: scoreDepthRule(rule) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score);

  return ranked[0]?.rule ?? null;
}

/**
 * Resolve a room-depth limit from the SQLite norm library.
 * Prefers numeric min/max rules about room depth (not loggia/balcony).
 */
export function resolveRoomDepthLimitFromLibrary(
  db: Database
): ResolvedRoomDepthLimit | null {
  const seen = new Map<string, StoredNormRule>();

  for (const topic of DEPTH_TOPICS) {
    const rules = queryNormRules(db, {
      topic,
      limit: 10,
    });
    for (const rule of rules) {
      if (!seen.has(rule.id)) seen.set(rule.id, rule);
    }
  }

  const rule = pickBestDepthRule([...seen.values()]);
  if (!rule) return null;

  // Room depth is a maximum constraint. We deliberately do NOT emit a minimum
  // depth: a «≥ X» rule (e.g. entryway width) applied as a max produces false
  // violations for every normal (deeper) room. No valid max → skip the check.
  const maxDepthMm = depthMaxOf(rule);
  if (maxDepthMm == null) return null;

  return {
    maxDepthMm,
    source: toAuditSource(rule.source),
    rule,
  };
}
