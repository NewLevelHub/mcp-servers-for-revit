import type { Database } from "better-sqlite3";
import { queryNormRules, type StoredNormRule } from "../rulesStore.js";
import {
  curatedRulesAsStored,
  ensureCuratedResidentialRoomNorms,
} from "./curatedResidentialRoomNorms.js";
import type { NormAuditSource } from "./types.js";
import { toAuditSource } from "./types.js";

export interface ResolvedRoomHeightLimit {
  minHeightMm: number;
  source: NormAuditSource;
  rule: StoredNormRule;
}

const ROOM_HEIGHT_TOPICS = [
  "высота жилых помещений от пола до потолка",
  "высота потолка жилых помещений 2,5",
  "минимальная высота помещения жилое",
  "высота жилых помещений от пола до низа потолков",
] as const;

const MIN_PLAUSIBLE_ROOM_HEIGHT_MM = 2200;
const MAX_PLAUSIBLE_ROOM_HEIGHT_MM = 3600;

function ruleBlob(rule: StoredNormRule): string {
  return `${rule.object} ${(rule.tags ?? []).join(" ")} ${rule.source.quote} ${rule.applicability?.raw ?? ""}`.toLowerCase();
}

function heightMinMmOf(rule: StoredNormRule): number | undefined {
  const fromNormalized = rule.normalized?.min ?? rule.normalized?.exact;
  if (fromNormalized != null && fromNormalized >= MIN_PLAUSIBLE_ROOM_HEIGHT_MM) {
    return fromNormalized;
  }
  if (
    (rule.type === "min_value" || rule.type === "exact_value") &&
    typeof rule.value === "number"
  ) {
    if (rule.unit === "mm" && rule.value >= MIN_PLAUSIBLE_ROOM_HEIGHT_MM) {
      return rule.value;
    }
    if (rule.unit === "m" && rule.value >= 2 && rule.value <= 3.6) {
      return rule.value * 1000;
    }
  }
  return undefined;
}

function isExcludedRoomHeightRule(rule: StoredNormRule): boolean {
  const blob = ruleBlob(rule);
  if (blob.includes("эвакуа")) return true;
  if (blob.includes("коридор") && !blob.includes("жил")) return true;
  if (blob.includes("мусор") || blob.includes("қоқыс")) return true;
  if (blob.includes("лифт")) return true;
  if (blob.includes("шахт")) return true;
  if (blob.includes("пандус")) return true;
  if (blob.includes("двер")) return true;
  if (blob.includes("огражден")) return true;
  if (blob.includes("лестниц") && !blob.includes("жил")) return true;
  if (rule.unit === "percent") return true;

  // Public baths / laundry / chemical — not residential apartments.
  if (
    /бан[яьие]|баня|банно|монша|прачеч|кір\s*жуу|химчист|химиялық|сауықтыру\s+кешен/i.test(
      blob
    )
  ) {
    if (
      !blob.includes("квартир") &&
      !blob.includes("пәтер") &&
      !blob.includes("многоквартир")
    ) {
      return true;
    }
  }

  // Prefer residential SP; exclude pure public-building height when tagged as such.
  if (
    (blob.includes("обществен") || blob.includes("қоғамдық")) &&
    !blob.includes("3.02-101") &&
    !/3\.02-101/.test(rule.source.document)
  ) {
    return true;
  }

  return false;
}

export function scoreRoomHeightRule(rule: StoredNormRule): number {
  const heightMm = heightMinMmOf(rule);
  if (heightMm == null) return -1000;
  if (
    heightMm < MIN_PLAUSIBLE_ROOM_HEIGHT_MM ||
    heightMm > MAX_PLAUSIBLE_ROOM_HEIGHT_MM
  ) {
    return -1000;
  }
  if (isExcludedRoomHeightRule(rule)) return -1000;

  const blob = ruleBlob(rule);
  if (
    !blob.includes("жил") &&
    !blob.includes("тұрғын") &&
    !blob.includes("помещен") &&
    !blob.includes("бөлме") &&
    !blob.includes("потолок") &&
    !blob.includes("төбе") &&
    !blob.includes("жай")
  ) {
    return -1000;
  }

  let score = 40;
  if (/3\.02-101/.test(rule.source.document)) score += 40;
  if (
    blob.includes("потолок") ||
    blob.includes("төбе") ||
    blob.includes("низа потолк")
  ) {
    score += 20;
  }
  if (blob.includes("от пола") || blob.includes("еден")) score += 15;
  if (heightMm >= 2400 && heightMm <= 2800) score += 25;
  if (heightMm === 2500) score += 20;
  // Penalize 3.6 m (laundry / large baths) even if it slipped past exclusions.
  if (heightMm >= 3300) score -= 40;
  if (rule.type === "min_value") score += 10;

  return score;
}

export function pickBestRoomHeightRule(
  rules: StoredNormRule[]
): StoredNormRule | null {
  const ranked = rules
    .map((rule) => ({ rule, score: scoreRoomHeightRule(rule) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score);
  return ranked[0]?.rule ?? null;
}

export function resolveRoomHeightLimitFromLibrary(
  db: Database
): ResolvedRoomHeightLimit | null {
  ensureCuratedResidentialRoomNorms(db);

  const seen = new Map<string, StoredNormRule>();
  for (const curated of curatedRulesAsStored()) {
    seen.set(curated.id, curated);
  }
  for (const topic of ROOM_HEIGHT_TOPICS) {
    for (const rule of queryNormRules(db, { topic, limit: 12 })) {
      if (!seen.has(rule.id)) seen.set(rule.id, rule);
    }
  }

  const rule = pickBestRoomHeightRule([...seen.values()]);
  if (!rule) return null;

  const minHeightMm = heightMinMmOf(rule);
  if (minHeightMm == null) return null;

  return {
    minHeightMm,
    source: toAuditSource(rule.source),
    rule,
  };
}
