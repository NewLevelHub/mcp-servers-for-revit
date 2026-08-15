/**
 * Curated residential area/height norms from СП РК 3.02-101-2012*
 * that PDF extractors routinely miss (table cells, "ванной - 2,25 м²" wording).
 *
 * Always merged into library resolution so check_room_norms / run_norm_audit
 * do not fall back to kitchen-niche 5 м² or public-bath 3,6 м.
 */
import type { Database } from "better-sqlite3";
import {
  saveNormRules,
  type SaveableNormRule,
  type StoredNormRule,
} from "../rulesStore.js";

export const CURATED_BATHROOM_AREA_RULE_ID =
  "сп рк 3.02-101-2012|таблица 1 примечание 4|ванная|min_value";

export const CURATED_ROOM_HEIGHT_RULE_ID =
  "сп рк 3.02-101-2012|таблица 1|жилое помещение|min_value";

const BATHROOM_QUOTE =
  "В минимальные площади квартир включены: жилая площадь 12 м² × N, минимальные площади кухни – 5 м², встроенных шкафов 0,6 м² - 1 м², ванной - 2,25 м², уборной - 1,2 м², прихожей - из расчета ширины не менее 1,2 м, коридора - из расчета общего количества комнат и наиболее компактной планировки.";

const HEIGHT_QUOTE =
  "Высота жилых помещений от пола до низа потолков: I класс — 3 м и более; II класс — 3,0 м; III класс — 2,7 м; IV класс — 2,5 м (СП РК 3.02-101-2012*, таблица 1 — классификация жилых зданий).";

/** Bathroom min area embedded in apartment area notes (табл. 1, прим. 4). */
export function curatedBathroomAreaRule(): SaveableNormRule {
  return {
    id: CURATED_BATHROOM_AREA_RULE_ID,
    type: "min_value",
    object: "ванная",
    value: 2.25,
    unit: "m2",
    source: {
      document: "СП РК 3.02-101-2012",
      clause: "табл. 1 прим. 4",
      quote: BATHROOM_QUOTE,
    },
    applicability: {
      raw: "минимальные площади квартир (многоквартирные жилые здания)",
      roomType: "жилые помещения",
      buildingType: "жилые здания",
    },
    normalized: { exact: 2.25 },
    tags: [
      "ванная",
      "санузел",
      "площадь ванной",
      "площадь санузла",
      "жуынатын бөлме",
      "санитарлық торап",
      "алаң",
      "жилая квартира",
    ],
  };
}

/**
 * Conservative residential clear height: lowest class-IV value from табл. 1
 * (2,5 м). Higher classes require more; use 2,5 м as the enforceable floor.
 */
export function curatedRoomHeightRule(): SaveableNormRule {
  return {
    id: CURATED_ROOM_HEIGHT_RULE_ID,
    type: "min_value",
    object: "жилое помещение",
    value: 2.5,
    unit: "m",
    source: {
      document: "СП РК 3.02-101-2012",
      clause: "табл. 1",
      quote: HEIGHT_QUOTE,
    },
    applicability: {
      raw: "высота жилых помещений от пола до низа потолков (классы I–IV)",
      roomType: "жилые помещения",
      buildingType: "жилые здания",
    },
    normalized: { exact: 2500 },
    tags: [
      "высота помещения",
      "высота жилых помещений",
      "потолок",
      "от пола до потолка",
      "биіктік",
      "еденнен төбеге",
      "тұрғын жай",
      "2,5 м",
    ],
  };
}

export const CURATED_ROOM_DEPTH_RULE_ID =
  "сп рк 3.02-101-2012|п. 4.4.10.22|жилое помещение|max_value";

export const CURATED_APARTMENT_DEPTH_RULE_ID =
  "сп рк 3.02-101-2012|п. 4.4.10.21|квартира|max_value";

/**
 * Room depth. The clause is printed in Kazakh only in СП РК 3.02-101-2012*, which
 * is why the Russian-oriented PDF extractor never produced a value and
 * check_room_depth reported "no numeric values in the library".
 *
 * The clause says "ұсынылады" (recommended), not "тиіс" (required), so 6 m is an
 * advisory threshold: a finding worth looking at, not a violation to certify.
 */
export function curatedRoomDepthRule(): SaveableNormRule {
  return {
    id: CURATED_ROOM_DEPTH_RULE_ID,
    type: "max_value",
    object: "жилое помещение",
    value: 6,
    unit: "m",
    source: {
      document: "СП РК 3.02-101-2012",
      clause: "п. 4.4.10.22",
      quote:
        "Тереңдігі 6 м асатын тұрғын үй бӛлмелерін кемінде 4 м енмен жобалау ұсынылады. " +
        "Тар және терең бӛлмелер ұсынылмайды.",
    },
    applicability: {
      raw: "глубина жилых комнат (рекомендация; при глубине > 6 м ширина не менее 4 м)",
      roomType: "жилые помещения",
      buildingType: "жилые здания",
    },
    normalized: { exact: 6000 },
    tags: [
      "глубина помещения",
      "глубина комнаты",
      "глубина жилой комнаты",
      "тереңдігі",
      "тұрғын бөлме",
      "узкая комната",
      "6 м",
    ],
  };
}

/**
 * Apartment depth measured from the window — a hard requirement ("тиіс"), unlike
 * the room-depth recommendation above. Also Kazakh-only in the source.
 */
export function curatedApartmentDepthRule(): SaveableNormRule {
  return {
    id: CURATED_APARTMENT_DEPTH_RULE_ID,
    type: "max_value",
    object: "квартира",
    value: 10,
    unit: "m",
    source: {
      document: "СП РК 3.02-101-2012",
      clause: "п. 4.4.10.21",
      quote:
        "Табиғи жарықтандыру және желдету шарттары бойынша бір пәтердің тереңдігі терезеден " +
        "10 м аспауы тиіс.",
    },
    applicability: {
      raw: "глубина квартиры от окна по условиям естественного освещения и проветривания",
      roomType: "квартира",
      buildingType: "жилые здания",
    },
    normalized: { exact: 10000 },
    tags: [
      "глубина квартиры",
      "глубина от окна",
      "естественное освещение",
      "проветривание",
      "пәтердің тереңдігі",
      "10 м",
    ],
  };
}

export function curatedResidentialRoomNorms(): SaveableNormRule[] {
  return [
    curatedBathroomAreaRule(),
    curatedRoomHeightRule(),
    curatedRoomDepthRule(),
    curatedApartmentDepthRule(),
  ];
}

/** Upsert curated rules into SQLite (idempotent). */
export function ensureCuratedResidentialRoomNorms(db: Database): {
  inserted: number;
  updated: number;
} {
  return saveNormRules(db, curatedResidentialRoomNorms(), {
    documentVersion: "27.04.2021",
  });
}

/** Convert saveable curated rules to StoredNormRule for in-memory scoring. */
export function curatedRulesAsStored(): StoredNormRule[] {
  const now = Date.now();
  return curatedResidentialRoomNorms().map((rule) => ({
    ...rule,
    id: rule.id,
    ruleKey: rule.id,
    documentVersion: "27.04.2021",
    createdAt: now,
    updatedAt: now,
    applicability: rule.applicability ?? null,
    tags: rule.tags ?? [],
  }));
}
