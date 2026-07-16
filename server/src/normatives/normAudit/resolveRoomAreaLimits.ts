import type { Database } from "better-sqlite3";
import { queryNormRules, type StoredNormRule } from "../rulesStore.js";
import {
  curatedRulesAsStored,
  ensureCuratedResidentialRoomNorms,
} from "./curatedResidentialRoomNorms.js";
import type { ResidentialRoomCategory } from "./roomPurpose.js";
import type { NormAuditSource } from "./types.js";
import { toAuditSource } from "./types.js";
import { parseAreaMinM2FromRule } from "./normAreaParsing.js";

export interface RoomAreaLimit {
  category: ResidentialRoomCategory;
  minAreaM2: number;
  source: NormAuditSource;
  rule: StoredNormRule;
}

const AREA_TOPICS: Record<
  Exclude<ResidentialRoomCategory, "excluded" | "unknown">,
  readonly string[]
> = {
  living_room: [
    "площадь жилого помещения комнаты",
    "площадь жилой комнаты 9",
    "минимальная площадь комнаты квартира",
  ],
  kitchen: ["площадь кухни минимальная", "кухня не менее 8 м2"],
  bathroom: [
    "площадь ванной 2,25",
    "площадь санузла",
    "площадь ванной комнаты",
    "ванной - 2,25",
  ],
  bedroom: ["площадь спальни", "площадь спален 8 м2"],
};

const CATEGORY_HINTS: Record<
  Exclude<ResidentialRoomCategory, "excluded" | "unknown">,
  readonly string[]
> = {
  living_room: ["жил", "комнат", "тұрғын", "living", "гостин"],
  kitchen: ["кухн", "ас үй", "kitchen"],
  bathroom: ["сануз", "ванн", "туалет", "wc", "душевая", "жуынатын"],
  bedroom: ["спальн", "bedroom", "жатын"],
};

function ruleBlob(rule: StoredNormRule): string {
  return `${rule.object} ${(rule.tags ?? []).join(" ")} ${rule.source.quote} ${rule.applicability?.raw ?? ""}`.toLowerCase();
}

function isExcludedAreaRule(rule: StoredNormRule): boolean {
  const blob = ruleBlob(rule);
  if (blob.includes("лоджи") || blob.includes("балкон")) return true;
  if (blob.includes("тамбур") && !blob.includes("ванн") && !blob.includes("кухн")) {
    return true;
  }
  // Corridor-only rules — not apartment room lists that merely mention коридор.
  if (
    (blob.includes("коридор") || blob.includes("дәліз")) &&
    !blob.includes("ванн") &&
    !blob.includes("кухн") &&
    !blob.includes("жил") &&
    !blob.includes("комнат")
  ) {
    return true;
  }
  if (blob.includes("контейнер") || blob.includes("қоқыс")) return true;
  if (blob.includes("терез") && !blob.includes("площад")) return true;
  if (rule.unit === "percent") return true;
  if (rule.unit === "mm" && !blob.includes("м 2") && !blob.includes("м²")) return true;
  return false;
}

function isKitchenOnlyRule(blob: string): boolean {
  const hasKitchen = blob.includes("кухн") || blob.includes("ас үй");
  const hasBathroom =
    blob.includes("ванн") ||
    blob.includes("сануз") ||
    blob.includes("туалет") ||
    blob.includes("уборн");
  return hasKitchen && !hasBathroom;
}

function isBathroomSpecific(blob: string): boolean {
  return (
    blob.includes("ванн") ||
    blob.includes("сануз") ||
    blob.includes("туалет") ||
    blob.includes("уборн") ||
    blob.includes("душевая") ||
    blob.includes("жуынатын")
  );
}

export function scoreRoomAreaRule(
  rule: StoredNormRule,
  category: Exclude<ResidentialRoomCategory, "excluded" | "unknown">
): number {
  const areaM2 = parseAreaMinM2FromRule(rule);
  if (areaM2 == null) return -1000;
  if (isExcludedAreaRule(rule)) return -1000;

  const blob = ruleBlob(rule);

  // Kitchen-niche / kitchen-only rules must never become bathroom limits.
  if (category === "bathroom") {
    if (isKitchenOnlyRule(blob)) return -1000;
    if (!isBathroomSpecific(blob)) return -1000;
  } else {
    const hints = CATEGORY_HINTS[category];
    if (!hints.some((hint) => blob.includes(hint))) return -1000;
  }

  // Bathroom rule must not win for kitchen / living / bedroom.
  if (category !== "bathroom" && isBathroomSpecific(blob) && !blob.includes("кухн")) {
    if (category === "kitchen" || category === "living_room" || category === "bedroom") {
      // Allow mixed quotes that also mention the category.
      const hints = CATEGORY_HINTS[category];
      if (!hints.some((hint) => blob.includes(hint))) return -1000;
    }
  }

  let score = 40;
  if (rule.type === "min_value") score += 15;
  if (/3\.02-101/.test(rule.source.document)) score += 25;
  else if (/3\.06-31/.test(rule.source.document)) score += 10;
  if (blob.includes("площад") || blob.includes("аудан")) score += 15;
  if (blob.includes("квартир") || blob.includes("пәтер")) score += 10;

  if (category === "living_room" && areaM2 >= 8 && areaM2 <= 12) score += 15;
  if (category === "kitchen" && areaM2 >= 5 && areaM2 <= 12) score += 15;
  if (category === "bedroom" && areaM2 >= 7 && areaM2 <= 14) score += 15;
  if (category === "bathroom" && areaM2 >= 2 && areaM2 <= 4) score += 25;
  if (category === "bathroom" && Math.abs(areaM2 - 2.25) < 0.01) score += 30;

  return score;
}

export function pickBestRoomAreaRule(
  rules: StoredNormRule[],
  category: Exclude<ResidentialRoomCategory, "excluded" | "unknown">
): StoredNormRule | null {
  const ranked = rules
    .map((rule) => ({ rule, score: scoreRoomAreaRule(rule, category) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score);
  return ranked[0]?.rule ?? null;
}

function resolveCategoryLimit(
  db: Database,
  category: Exclude<ResidentialRoomCategory, "excluded" | "unknown">
): RoomAreaLimit | null {
  ensureCuratedResidentialRoomNorms(db);

  const seen = new Map<string, StoredNormRule>();
  for (const curated of curatedRulesAsStored()) {
    seen.set(curated.id, curated);
  }
  for (const topic of AREA_TOPICS[category]) {
    for (const rule of queryNormRules(db, { topic, limit: 12 })) {
      if (!seen.has(rule.id)) seen.set(rule.id, rule);
    }
  }

  const rule = pickBestRoomAreaRule([...seen.values()], category);
  if (!rule) return null;

  const minAreaM2 = parseAreaMinM2FromRule(rule);
  if (minAreaM2 == null) return null;

  return {
    category,
    minAreaM2,
    source: toAuditSource(rule.source),
    rule,
  };
}

/** Resolve min area limits per residential room category from the norm library. */
export function resolveRoomAreaLimitsFromLibrary(
  db: Database
): RoomAreaLimit[] {
  const categories: Array<
    Exclude<ResidentialRoomCategory, "excluded" | "unknown">
  > = ["living_room", "kitchen", "bathroom", "bedroom"];

  const limits: RoomAreaLimit[] = [];
  for (const category of categories) {
    const limit = resolveCategoryLimit(db, category);
    if (limit) limits.push(limit);
  }
  return limits;
}

export function limitForCategory(
  limits: RoomAreaLimit[],
  category: ResidentialRoomCategory
): RoomAreaLimit | undefined {
  if (category === "excluded" || category === "unknown") return undefined;
  return limits.find((limit) => limit.category === category);
}
