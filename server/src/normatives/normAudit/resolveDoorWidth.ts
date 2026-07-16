import type { Database } from "better-sqlite3";
import { queryNormRules, type StoredNormRule } from "../rulesStore.js";
import type { NormAuditSource } from "./types.js";
import { toAuditSource } from "./types.js";

export interface ResolvedDoorWidthLimit {
  minWidthMm: number;
  source: NormAuditSource;
  rule: StoredNormRule;
}

const DOOR_WIDTH_TOPICS = [
  "ширина эвакуационного выхода двери",
  "ширина дверного проёма эвакуация",
  "ширина двери выход на лестничную клетку",
] as const;

/**
 * Plausible band for a minimum egress DOOR/opening width (mm). A door leaf /
 * opening min is ~0.8–1.2 m; values like 1.35 m are stair marches and 2.4 m are
 * theatre-hall doors — neither is a generic egress-door minimum.
 */
const MIN_PLAUSIBLE_DOOR_WIDTH_MM = 700;
const MAX_PLAUSIBLE_DOOR_WIDTH_MM = 1500;

function ruleBlob(rule: StoredNormRule): string {
  return `${rule.object} ${rule.source.quote} ${rule.applicability?.raw ?? ""}`.toLowerCase();
}

/** Extract a MINIMUM width in mm from the rule (normalized mm preferred). */
function widthMinMmOf(rule: StoredNormRule): number | undefined {
  const fromNormalized = rule.normalized?.min ?? rule.normalized?.exact;
  if (fromNormalized != null) return fromNormalized;
  // Fallback: min/exact rule whose value is metres in the library.
  if (
    (rule.type === "min_value" || rule.type === "exact_value") &&
    typeof rule.value === "number"
  ) {
    if (rule.unit === "mm") return rule.value;
    if (rule.unit === "m") return rule.value * 1000;
  }
  return undefined;
}

/** Reject stair marches, ramps, railings, windows mistaken for a door width. */
function isExcludedDoorWidthRule(rule: StoredNormRule): boolean {
  const blob = ruleBlob(rule);
  if (blob.includes("марш")) return true; // ширина марша лестницы
  if (blob.includes("пандус")) return true;
  if (blob.includes("ограждени") || blob.includes("перил")) return true;
  if (blob.includes("подоконник")) return true;
  // Window openings, not doors. «проём» alone is generic (окно/дверь), so a
  // window rule is only kept out if it lacks a strong door word (двер/есік).
  if (mentionsWindow(blob) && !hasStrongDoorWord(blob)) return true;
  return false;
}

function mentionsWindow(blob: string): boolean {
  return (
    blob.includes("окон") ||
    blob.includes("окно") ||
    blob.includes("оконн") ||
    blob.includes("терезе")
  );
}

/** Unambiguous door words — «проём»/«ойық» are generic openings, excluded here. */
function hasStrongDoorWord(blob: string): boolean {
  return (
    blob.includes("двер") ||
    blob.includes("дверн") ||
    blob.includes("есік")
  );
}

function hasDoorWord(blob: string): boolean {
  return (
    hasStrongDoorWord(blob) ||
    blob.includes("проём") ||
    blob.includes("проем") ||
    blob.includes("ойық") ||
    blob.includes("ойығ")
  );
}

function hasWidthWord(blob: string): boolean {
  return blob.includes("ширин") || blob.includes("ені") || blob.includes("width");
}

function hasEgressWord(blob: string): boolean {
  return (
    blob.includes("эвакуац") ||
    blob.includes("эвакуациял") ||
    blob.includes("выход") ||
    blob.includes("шығу") ||
    blob.includes("баспалдақ") ||
    blob.includes("лестничн")
  );
}

/**
 * Score a rule as an egress door-width minimum. Returns <=0 to disqualify.
 * Requires: a door/opening subject, a width word, and a plausible numeric min.
 */
export function scoreDoorWidthRule(rule: StoredNormRule): number {
  const minMm = widthMinMmOf(rule);
  if (minMm == null) return -1000;
  if (minMm < MIN_PLAUSIBLE_DOOR_WIDTH_MM || minMm > MAX_PLAUSIBLE_DOOR_WIDTH_MM) {
    return -1000;
  }
  if (isExcludedDoorWidthRule(rule)) return -1000;

  const blob = ruleBlob(rule);
  if (!hasDoorWord(blob)) return -1000;
  if (!hasWidthWord(blob)) return -1000;
  // A min-only / exact / range rule can bound a minimum; a pure max cannot.
  if (rule.type === "max_value") return -1000;

  let score = 40;
  if (hasEgressWord(blob)) score += 20;
  if (rule.object.toLowerCase().includes("двер")) score += 10;
  // Prefer the residential-buildings code (СП РК 3.02-101) for applicability.
  if (/3\.02-101/.test(rule.source.document)) score += 25;
  if (blob.includes("жил") || blob.includes("тұрғын")) score += 8;
  if (rule.type === "min_value") score += 10;
  // Typical egress door leaf minimum is 0.8–1.0 m.
  if (minMm >= 800 && minMm <= 1000) score += 10;
  if (rule.source.clause) score += 5;

  return score;
}

export function pickBestDoorWidthRule(
  rules: StoredNormRule[]
): StoredNormRule | null {
  const ranked = rules
    .map((rule) => ({ rule, score: scoreDoorWidthRule(rule) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score);

  return ranked[0]?.rule ?? null;
}

/**
 * Resolve a minimum egress door/opening width from the SQLite norm library.
 * Returns null (→ check skipped, never a fabricated number) when no plausible
 * door-width rule is found.
 */
export function resolveDoorWidthLimitFromLibrary(
  db: Database
): ResolvedDoorWidthLimit | null {
  const seen = new Map<string, StoredNormRule>();
  for (const topic of DOOR_WIDTH_TOPICS) {
    for (const rule of queryNormRules(db, { topic, limit: 12 })) {
      if (!seen.has(rule.id)) seen.set(rule.id, rule);
    }
  }

  const rule = pickBestDoorWidthRule([...seen.values()]);
  if (!rule) return null;

  const minWidthMm = widthMinMmOf(rule);
  if (minWidthMm == null) return null;

  return {
    minWidthMm,
    source: toAuditSource(rule.source),
    rule,
  };
}
