import type { Database } from "better-sqlite3";
import { queryNormRules, type StoredNormRule } from "../rulesStore.js";
import type { NormAuditSource } from "./types.js";
import { toAuditSource } from "./types.js";

function ruleBlob(rule: StoredNormRule): string {
  return `${rule.object} ${rule.source.quote} ${rule.applicability?.raw ?? ""}`.toLowerCase();
}

function lengthMinMm(rule: StoredNormRule): number | undefined {
  const n = rule.normalized?.min ?? rule.normalized?.exact;
  if (n != null) return n;
  if (
    (rule.type === "min_value" || rule.type === "exact_value") &&
    typeof rule.value === "number"
  ) {
    if (rule.unit === "mm") return rule.value;
    if (rule.unit === "m") return rule.value * 1000;
  }
  return undefined;
}

function lengthMaxMm(rule: StoredNormRule): number | undefined {
  const n = rule.normalized?.max ?? rule.normalized?.exact;
  if (n != null && rule.type === "max_value") return n;
  if (rule.type === "max_value" && typeof rule.value === "number") {
    if (rule.unit === "mm") return rule.value;
    if (rule.unit === "m") return rule.value * 1000;
  }
  if (rule.type === "range") {
    return rule.normalized?.max;
  }
  return undefined;
}

function collect(db: Database, topics: readonly string[]): StoredNormRule[] {
  const seen = new Map<string, StoredNormRule>();
  for (const topic of topics) {
    for (const rule of queryNormRules(db, { topic, limit: 12 })) {
      if (!seen.has(rule.id)) seen.set(rule.id, rule);
    }
  }
  return [...seen.values()];
}

/**
 * МГН stair clauses live in СП РК 3.06-101 (the catalog carries it as both
 * «СП РК 3.06-101» and «СП РК 3.06-101-2012»). They are stricter than the
 * general ones — проступь ≥ 300 мм against ≥ 250 мм, марш ≥ 1,35 м for открытые
 * лестницы — so applying them to ordinary housing reports a violation the
 * general code does not have. That is exactly what run_norm_audit did while its
 * own checklist reported «проверки МГН по умолчанию выключены».
 *
 * Matched on the document only. PDF extraction bleeds МГН wording
 * («кресло-коляска», «инвалида-колясочника») into unrelated quotes — п. 9.7 of
 * SP RK 3.06-31-2005 is a live example — so a keyword match would drop good rules.
 */
const ACCESSIBILITY_DOCUMENT_RE = /3\.06-101/;

export function isAccessibilityRule(rule: StoredNormRule): boolean {
  return ACCESSIBILITY_DOCUMENT_RE.test(rule.source?.document ?? "");
}

export interface StairRuleScope {
  /**
   * Include МГН sources. Default false — ordinary housing, matching the
   * `optInOnly` МГН checkers in checklist.ts.
   */
  includeAccessibility?: boolean;
}

/** Drops МГН rules unless asked for, and reports how many were dropped. */
function scopeRules(
  rules: StoredNormRule[],
  scope?: StairRuleScope
): { kept: StoredNormRule[]; skippedMgnRules: number } {
  if (scope?.includeAccessibility) return { kept: rules, skippedMgnRules: 0 };
  const kept = rules.filter((rule) => !isAccessibilityRule(rule));
  return { kept, skippedMgnRules: rules.length - kept.length };
}

export interface ResolvedStairWidthLimit {
  minWidthMm: number;
  source: NormAuditSource;
  rule: StoredNormRule;
  /** МГН candidates dropped because scope is ordinary housing. */
  skippedMgnRules?: number;
}

export function resolveStairWidthLimitFromLibrary(
  db: Database,
  scope?: StairRuleScope
): ResolvedStairWidthLimit | null {
  const { kept: rules, skippedMgnRules } = scopeRules(
    collect(db, ["ширина марша лестницы", "ширина марша", "лестничный марш"]),
    scope
  );
  const ranked = rules
    .map((rule) => {
      const minMm = lengthMinMm(rule);
      if (minMm == null || minMm < 700 || minMm > 2000) return { rule, score: -1 };
      const blob = ruleBlob(rule);
      if (!blob.includes("марш") && !blob.includes("лестниц") && !blob.includes("баспалдақ")) {
        return { rule, score: -1 };
      }
      if (blob.includes("пандус") && !blob.includes("марш")) return { rule, score: -1 };
      let score = 40;
      if (blob.includes("эвак")) score += 20;
      if (minMm === 1050 || minMm === 1200 || minMm === 1350 || minMm === 900) score += 15;
      if (/3\.02-101|3\.06-31/.test(rule.source.document)) score += 20;
      if (rule.type === "min_value") score += 10;
      return { rule, score };
    })
    .filter((e) => e.score > 0)
    .sort((a, b) => b.score - a.score);

  const best = ranked[0]?.rule;
  if (!best) return null;
  const minWidthMm = lengthMinMm(best);
  if (minWidthMm == null) return null;
  return {
    minWidthMm,
    source: toAuditSource(best.source),
    rule: best,
    skippedMgnRules,
  };
}

export interface ResolvedStairRiserTreadLimits {
  maxRiserMm?: number;
  minTreadMm?: number;
  riserSource?: NormAuditSource;
  treadSource?: NormAuditSource;
  riserRule?: StoredNormRule;
  treadRule?: StoredNormRule;
  /** МГН candidates dropped because scope is ordinary housing. */
  skippedMgnRules?: number;
}

/** Bench / seating / rest furniture — not stair geometry (REV false positive). */
function isBenchOrSeatRule(rule: StoredNormRule): boolean {
  const blob = ruleBlob(rule);
  return (
    blob.includes("скамь") ||
    blob.includes("скамей") ||
    blob.includes("сидень") ||
    blob.includes("подлокотник") ||
    blob.includes("место отдыха") ||
    blob.includes("местах отдыха") ||
    blob.includes("bench") ||
    blob.includes("seat") ||
    blob.includes("armchair")
  );
}

function mentionsStairGeometry(rule: StoredNormRule): boolean {
  const blob = ruleBlob(rule);
  return (
    blob.includes("проступ") ||
    blob.includes("подступен") ||
    blob.includes("ступен") ||
    blob.includes("лестниц") ||
    blob.includes("баспалдақ") ||
    blob.includes("марш") ||
    blob.includes("tread") ||
    blob.includes("riser")
  );
}

/**
 * Score a rule as max stair riser height (подступенок).
 * Returns <=0 to disqualify (e.g. bench seat height).
 */
export function scoreStairRiserRule(rule: StoredNormRule): number {
  if (isBenchOrSeatRule(rule)) return -1000;
  if (!mentionsStairGeometry(rule)) return -1000;

  let maxMm =
    rule.type === "max_value" || rule.type === "range"
      ? lengthMaxMm(rule)
      : undefined;
  // Some texts say «не более 0,19 м» stored oddly as exact/min
  if (maxMm == null && rule.type === "max_value") {
    maxMm = lengthMinMm(rule);
  }
  if (maxMm == null || maxMm < 140 || maxMm > 220) return -1000;

  const blob = ruleBlob(rule);
  let score = 40;
  if (blob.includes("подступен")) score += 25;
  if (blob.includes("ступен") || blob.includes("лестниц")) score += 15;
  if (/3\.02-101|3\.06-31|3\.06-101/.test(rule.source.document)) score += 15;
  if (rule.type === "max_value") score += 10;
  if (maxMm >= 150 && maxMm <= 190) score += 10;
  return score;
}

/**
 * Score a rule as min stair tread depth (проступь).
 * Returns <=0 to disqualify (e.g. 0,38 м bench height mistaken for tread).
 */
export function scoreStairTreadRule(rule: StoredNormRule): number {
  if (isBenchOrSeatRule(rule)) return -1000;
  if (rule.type === "max_value") return -1000;
  if (!mentionsStairGeometry(rule)) return -1000;

  const minMm = lengthMinMm(rule);
  // Typical stair tread mins are ~250–300 mm; reject seat-height band (>350)
  if (minMm == null || minMm < 200 || minMm > 350) return -1000;

  const blob = ruleBlob(rule);
  let score = 40;
  if (blob.includes("проступ")) score += 30;
  if (blob.includes("ступен") || blob.includes("лестниц")) score += 15;
  if (/3\.02-101|3\.06-31|3\.06-101/.test(rule.source.document)) score += 15;
  if (rule.type === "min_value") score += 10;
  if (minMm >= 250 && minMm <= 300) score += 15;
  return score;
}

export function pickBestStairRiserRule(
  rules: StoredNormRule[],
  scope?: StairRuleScope
): StoredNormRule | null {
  const ranked = scopeRules(rules, scope)
    .kept.map((rule) => ({ rule, score: scoreStairRiserRule(rule) }))
    .filter((e) => e.score > 0)
    .sort((a, b) => b.score - a.score);
  return ranked[0]?.rule ?? null;
}

export function pickBestStairTreadRule(
  rules: StoredNormRule[],
  scope?: StairRuleScope
): StoredNormRule | null {
  const ranked = scopeRules(rules, scope)
    .kept.map((rule) => ({ rule, score: scoreStairTreadRule(rule) }))
    .filter((e) => e.score > 0)
    .sort((a, b) => b.score - a.score);
  return ranked[0]?.rule ?? null;
}

export function resolveStairRiserTreadLimitsFromLibrary(
  db: Database,
  scope?: StairRuleScope
): ResolvedStairRiserTreadLimits | null {
  const riserRules = collect(db, [
    "высота подступенка",
    "высота ступени",
    "подступенок",
  ]);
  const treadRules = collect(db, [
    "ширина проступи",
    "проступь",
    "глубина ступени",
  ]);

  // Counted before picking: on an ordinary-housing run the МГН проступь 300 мм
  // is often the only numeric candidate, and the caller has to be able to say so
  // instead of reporting a bare "нормы не найдены".
  const skippedMgnRules =
    scopeRules(riserRules, scope).skippedMgnRules +
    scopeRules(treadRules, scope).skippedMgnRules;

  const riserRule = pickBestStairRiserRule(riserRules, scope);
  const treadRule = pickBestStairTreadRule(treadRules, scope);

  let maxRiserMm: number | undefined;
  if (riserRule) {
    maxRiserMm =
      lengthMaxMm(riserRule) ??
      (riserRule.type === "max_value" ? lengthMinMm(riserRule) : undefined);
  }

  const minTreadMm = treadRule ? lengthMinMm(treadRule) : undefined;

  if (maxRiserMm == null && minTreadMm == null) return null;
  return {
    maxRiserMm,
    minTreadMm,
    riserSource: riserRule ? toAuditSource(riserRule.source) : undefined,
    treadSource: treadRule ? toAuditSource(treadRule.source) : undefined,
    riserRule: riserRule ?? undefined,
    treadRule: treadRule ?? undefined,
    skippedMgnRules,
  };
}

export interface ResolvedRampLimits {
  minWidthMm?: number;
  maxSlopePercent?: number;
  widthSource?: NormAuditSource;
  slopeSource?: NormAuditSource;
  widthRule?: StoredNormRule;
  slopeRule?: StoredNormRule;
}

export function resolveRampLimitsFromLibrary(
  db: Database
): ResolvedRampLimits | null {
  const widthRules = collect(db, ["ширина пандуса", "пандус ені", "ramp width"]);
  const slopeRules = collect(db, ["уклон пандуса", "еңіс пандус", "ramp slope"]);

  let minWidthMm: number | undefined;
  let widthRule: StoredNormRule | undefined;
  for (const rule of widthRules) {
    const min = lengthMinMm(rule);
    const blob = ruleBlob(rule);
    if (!blob.includes("пандус") && !blob.includes("ramp")) continue;
    if (min != null && min >= 900 && min <= 2000 && rule.type !== "max_value") {
      minWidthMm = min;
      widthRule = rule;
      break;
    }
  }

  let maxSlopePercent: number | undefined;
  let slopeRule: StoredNormRule | undefined;
  for (const rule of slopeRules) {
    const blob = ruleBlob(rule);
    if (!blob.includes("пандус") && !blob.includes("уклон") && !blob.includes("еңіс")) {
      continue;
    }
    if (rule.type === "max_value" && rule.unit === "percent" && typeof rule.value === "number") {
      if (rule.value > 0 && rule.value <= 15) {
        maxSlopePercent = rule.normalized?.exact ?? rule.value;
        slopeRule = rule;
        break;
      }
    }
    // quote may say 5% with normalized 5
    if (rule.normalized?.exact != null && rule.normalized.exact <= 15 && rule.type === "max_value") {
      maxSlopePercent = rule.normalized.exact;
      slopeRule = rule;
      break;
    }
  }

  if (minWidthMm == null && maxSlopePercent == null) return null;
  return {
    minWidthMm,
    maxSlopePercent,
    widthSource: widthRule ? toAuditSource(widthRule.source) : undefined,
    slopeSource: slopeRule ? toAuditSource(slopeRule.source) : undefined,
    widthRule,
    slopeRule,
  };
}

export interface ResolvedRailingHeightLimit {
  minHeightMm: number;
  source: NormAuditSource;
  rule: StoredNormRule;
}

export function resolveRailingHeightLimitFromLibrary(
  db: Database
): ResolvedRailingHeightLimit | null {
  const rules = collect(db, [
    "высота ограждения",
    "высота перил",
    "ограждение балкона",
    "railing height",
  ]);

  const ranked = rules
    .map((rule) => {
      const blob = ruleBlob(rule);
      if (
        !blob.includes("огражд") &&
        !blob.includes("перил") &&
        !blob.includes("қоршау") &&
        !blob.includes("railing")
      ) {
        return { rule, score: -1 };
      }
      // Prefer minimum (balcony fire railing 1.2) over handrail range 0.8-0.9
      let minMm =
        rule.normalized?.min ??
        (rule.type === "min_value" ? lengthMinMm(rule) : undefined);
      if (rule.type === "range" && rule.normalized?.min != null) {
        minMm = rule.normalized.min;
      }
      if (minMm == null || minMm < 700 || minMm > 1500) return { rule, score: -1 };
      let score = 40;
      if (minMm >= 1100) score += 25; // balcony/fire railing
      if (blob.includes("балкон") || blob.includes("лоджи")) score += 15;
      if (/3\.02-101|3\.06-101/.test(rule.source.document)) score += 15;
      if (rule.type === "min_value") score += 10;
      return { rule, score, minMm };
    })
    .filter((e) => e.score > 0)
    .sort((a, b) => b.score - a.score);

  const best = ranked[0];
  if (!best || best.minMm == null) return null;
  return {
    minHeightMm: best.minMm,
    source: toAuditSource(best.rule.source),
    rule: best.rule,
  };
}
