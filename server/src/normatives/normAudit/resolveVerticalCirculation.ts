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

export interface ResolvedStairWidthLimit {
  minWidthMm: number;
  source: NormAuditSource;
  rule: StoredNormRule;
}

export function resolveStairWidthLimitFromLibrary(
  db: Database
): ResolvedStairWidthLimit | null {
  const rules = collect(db, [
    "ширина марша лестницы",
    "ширина марша",
    "лестничный марш",
  ]);
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
  return { minWidthMm, source: toAuditSource(best.source), rule: best };
}

export interface ResolvedStairRiserTreadLimits {
  maxRiserMm?: number;
  minTreadMm?: number;
  riserSource?: NormAuditSource;
  treadSource?: NormAuditSource;
  riserRule?: StoredNormRule;
  treadRule?: StoredNormRule;
}

export function resolveStairRiserTreadLimitsFromLibrary(
  db: Database
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

  let maxRiserMm: number | undefined;
  let riserRule: StoredNormRule | undefined;
  for (const rule of riserRules) {
    const max = lengthMaxMm(rule) ?? lengthMinMm(rule);
    // Prefer max-style for riser; if only min found skip
    if (rule.type === "max_value" || rule.type === "range") {
      const v = lengthMaxMm(rule);
      if (v != null && v >= 140 && v <= 220) {
        maxRiserMm = v;
        riserRule = rule;
        break;
      }
    }
    // Some texts say "не более 0,19 м"
    if (rule.type === "max_value") {
      const v = lengthMinMm(rule); // sometimes stored as exact
      if (v != null && v >= 140 && v <= 220) {
        maxRiserMm = v;
        riserRule = rule;
        break;
      }
    }
  }

  let minTreadMm: number | undefined;
  let treadRule: StoredNormRule | undefined;
  for (const rule of treadRules) {
    const min = lengthMinMm(rule);
    if (min != null && min >= 200 && min <= 400 && rule.type !== "max_value") {
      minTreadMm = min;
      treadRule = rule;
      break;
    }
  }

  if (maxRiserMm == null && minTreadMm == null) return null;
  return {
    maxRiserMm,
    minTreadMm,
    riserSource: riserRule ? toAuditSource(riserRule.source) : undefined,
    treadSource: treadRule ? toAuditSource(treadRule.source) : undefined,
    riserRule,
    treadRule,
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
