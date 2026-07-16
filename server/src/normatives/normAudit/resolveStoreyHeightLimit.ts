import type { Database } from "better-sqlite3";
import { queryNormRules, type StoredNormRule } from "../rulesStore.js";
import type { NormAuditSource } from "./types.js";
import { toAuditSource } from "./types.js";

export interface ResolvedStoreyHeightLimit {
  minStoreyHeightMm: number;
  source: NormAuditSource;
  rule: StoredNormRule;
}

const STOREY_HEIGHT_TOPICS = [
  "высота этажа жилого здания",
  "высота жилого этажа от пола до пола",
  "высота этажа в свету жилые",
] as const;

const MIN_PLAUSIBLE_STOREY_MM = 2400;
const MAX_PLAUSIBLE_STOREY_MM = 4500;

function ruleBlob(rule: StoredNormRule): string {
  return `${rule.object} ${rule.source.quote} ${rule.applicability?.raw ?? ""}`.toLowerCase();
}

function storeyMinMmOf(rule: StoredNormRule): number | undefined {
  const fromNormalized = rule.normalized?.min ?? rule.normalized?.exact;
  if (
    fromNormalized != null &&
    fromNormalized >= MIN_PLAUSIBLE_STOREY_MM &&
    fromNormalized <= MAX_PLAUSIBLE_STOREY_MM
  ) {
    return fromNormalized;
  }
  if (
    (rule.type === "min_value" || rule.type === "exact_value") &&
    typeof rule.value === "number"
  ) {
    if (rule.unit === "mm" && rule.value >= MIN_PLAUSIBLE_STOREY_MM) {
      return rule.value;
    }
    if (rule.unit === "m" && rule.value >= 2.4 && rule.value <= 4.5) {
      return rule.value * 1000;
    }
  }
  return undefined;
}

function isExcludedStoreyHeightRule(rule: StoredNormRule): boolean {
  const blob = ruleBlob(rule);
  if (blob.includes("эвакуа")) return true;
  if (blob.includes("подвал")) return true;
  if (blob.includes("шахт")) return true;
  if (blob.includes("лифт")) return true;
  if (blob.includes("пандус")) return true;
  if (blob.includes("марш")) return true;
  if (rule.unit === "percent") return true;
  return false;
}

export function scoreStoreyHeightRule(rule: StoredNormRule): number {
  const heightMm = storeyMinMmOf(rule);
  if (heightMm == null) return -1000;
  if (isExcludedStoreyHeightRule(rule)) return -1000;

  const blob = ruleBlob(rule);
  if (
    !blob.includes("этаж") &&
    !blob.includes("қабат") &&
    !blob.includes("storey")
  ) {
    return -1000;
  }

  let score = 35;
  if (/3\.02-101/.test(rule.source.document)) score += 25;
  if (blob.includes("жил") || blob.includes("тұрғын")) score += 20;
  if (blob.includes("от пола") || blob.includes("еден")) score += 15;
  if (heightMm >= 2500 && heightMm <= 3300) score += 15;
  if (rule.type === "min_value") score += 10;

  return score;
}

export function pickBestStoreyHeightRule(
  rules: StoredNormRule[]
): StoredNormRule | null {
  const ranked = rules
    .map((rule) => ({ rule, score: scoreStoreyHeightRule(rule) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score);
  return ranked[0]?.rule ?? null;
}

export function resolveStoreyHeightLimitFromLibrary(
  db: Database
): ResolvedStoreyHeightLimit | null {
  const seen = new Map<string, StoredNormRule>();
  for (const topic of STOREY_HEIGHT_TOPICS) {
    for (const rule of queryNormRules(db, { topic, limit: 12 })) {
      if (!seen.has(rule.id)) seen.set(rule.id, rule);
    }
  }

  const rule = pickBestStoreyHeightRule([...seen.values()]);
  if (!rule) return null;

  const minStoreyHeightMm = storeyMinMmOf(rule);
  if (minStoreyHeightMm == null) return null;

  return {
    minStoreyHeightMm,
    source: toAuditSource(rule.source),
    rule,
  };
}
