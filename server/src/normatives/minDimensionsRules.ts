import { createHash } from "node:crypto";
import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import pdfParse from "pdf-parse";
import type { NormativeSourceRef } from "./types.js";
import {
  DEFAULT_FIRE_DOOR_PDF_FILES,
  normalizeDocumentName,
  resolveNormativesDir,
} from "./fireDoorRules.js";

/** Pilot PDF set — residential + fire norms with balcony/loggia/pier rules. */
export const DEFAULT_MIN_DIMENSIONS_PDF_FILES = [
  ...DEFAULT_FIRE_DOOR_PDF_FILES,
  "SP_RK_3.06-101-2012_27.11.2019.pdf",
  "SP_RK_3.06-31-2005.pdf",
] as const;

export type MinDimensionObject =
  | "балкон"
  | "лоджия"
  | "противопожарный простенок";

export type MinDimensionMetric =
  | "width"
  | "depth"
  | "pier_to_opening"
  | "pier_between_openings";

export interface MinDimensionNormRule {
  id: string;
  object: MinDimensionObject;
  metric: MinDimensionMetric;
  minValueMm: number;
  source: NormativeSourceRef;
  sourcePdf: string;
  applicability?: string;
}

const CLAUSE_PREFIX_RE =
  /(?:^|\s)(?:п\.?\s*|§\s*)(\d+(?:\.\d+)*)\s*[.:)\-–—]?\s*/i;

const MIN_VALUE_RE =
  /(?:не\s+менее|кемінде|кем\s+емес|≥|>=)\s*(\d[\d\s,.]*)\s*(мм|mm|м(?:\s|\.|,|$)|m(?:\s|\.|,|$))/i;

function parseNumber(raw: string): number {
  return Number(raw.replace(/\s/g, "").replace(",", "."));
}

function extractClause(sentence: string): string {
  const match = sentence.match(CLAUSE_PREFIX_RE);
  return match ? `п. ${match[1]}` : "";
}

function parseAllMinValuesMm(sentence: string): Array<{ valueMm: number; index: number }> {
  const results: Array<{ valueMm: number; index: number }> = [];
  const pattern =
    /(?:не\s+менее|кемінде|кем\s+емес|≥|>=)\s*(\d[\d\s,.]*)\s*(мм|mm|м(?:\s|\.|,|$)|m(?:\s|\.|,|$))/gi;

  for (const match of sentence.matchAll(pattern)) {
    const value = parseNumber(match[1]);
    const unit = match[2].toLowerCase();
    let valueMm = value;
    if (unit.startsWith("м") || unit.startsWith("m")) {
      const after = sentence.slice(match.index! + match[0].length);
      if (/^\s*3|³/.test(after)) continue;
      valueMm = value * 1000;
    }
    if (valueMm > 0) {
      results.push({ valueMm, index: match.index ?? 0 });
    }
  }

  return results;
}

function pickValueForMetric(
  sentence: string,
  metric: MinDimensionMetric
): number | null {
  const values = parseAllMinValuesMm(sentence);
  if (values.length === 0) return parseMinValueMm(sentence);
  if (values.length === 1) return values[0].valueMm;

  const lower = sentence.toLowerCase();
  if (metric === "pier_between_openings") {
    const betweenIdx = Math.max(
      lower.indexOf("между"),
      lower.indexOf("арасы"),
      lower.indexOf("арасында")
    );
    if (betweenIdx >= 0) {
      const after = values.filter((v) => v.index >= betweenIdx);
      if (after.length > 0) return after[0].valueMm;
      return values[values.length - 1].valueMm;
    }
  }

  if (metric === "pier_to_opening") {
    const beforeIdx = Math.min(
      ...[
        lower.indexOf("торца"),
        lower.indexOf("до окон"),
        lower.indexOf("простенк"),
        lower.indexOf("ойықтан"),
      ].filter((i) => i >= 0)
    );
    if (Number.isFinite(beforeIdx) && beforeIdx >= 0) {
      const before = values.filter((v) => v.index <= beforeIdx + 40);
      if (before.length > 0) return before[before.length - 1].valueMm;
    }
    return values[0].valueMm;
  }

  return values[0].valueMm;
}

function parseMinValueMm(sentence: string): number | null {
  const match = sentence.match(MIN_VALUE_RE);
  if (!match) {
    const looseM = sentence.match(/(\d+[,.]\d+)\s*м\b/i);
    if (looseM && /не\s+менее|кемінде|кем\s+емес/i.test(sentence)) {
      const after = sentence.slice((looseM.index ?? 0) + looseM[0].length);
      if (/^\s*3|³/.test(after)) return null;
      return parseNumber(looseM[1]) * 1000;
    }
    return null;
  }

  const value = parseNumber(match[1]);
  const unit = match[2].toLowerCase();
  if (unit === "мм" || unit === "mm") return value;
  if (unit.startsWith("м") || unit.startsWith("m")) {
    const after = sentence.slice(match.index! + match[0].length);
    if (/^\s*3|³/.test(after)) return null;
    return value * 1000;
  }
  return value;
}

function extractApplicability(sentence: string): string | undefined {
  const match = sentence.match(
    /(?:для\s+[^.;]+|в\s+квартирах\s+для\s+[^.;]+|в\s+жилых\s+зданиях[^.;]*|класс[а-яё]*\s*ф[^.;]+|қарт\s+және\s+мүгедек[^.;]+|престар[^.;]+|инвалид[^.;]{0,80})/i
  );
  return match ? match[0].replace(/\s+/g, " ").trim() : undefined;
}

/** МГН / спецжильё / пожилые — не для массового жилого дома. */
export function isMgnOrSpecialHousingRule(rule: MinDimensionNormRule): boolean {
  const blob =
    `${rule.source.document} ${rule.sourcePdf} ${rule.source.clause} ${rule.source.quote} ${rule.applicability ?? ""}`.toLowerCase();

  if (/3\.06-101|3\.06-31/.test(blob)) return true;
  if (
    /престар|инвалид|мүгедек|қарт\s+және|маломобил|коляск|специальн\w*\s+квартир|спецжил|арнайы\s+(?:пәтер|квартир)/i.test(
      blob
    )
  ) {
    return true;
  }
  // СП РК 3.02-101 п. 4.6.5 — раздел доступности МГН.
  if (/п\.?\s*4\.6\.5\b/.test(blob) || /\b4\.6\.5\b/.test(rule.source.clause)) {
    return true;
  }
  return false;
}

/**
 * Ширина балкона/галереи к незадымляемой ЛК (Н1) — п. 4.2.30.
 * Применяется только к путям эвакуации (воздушная зона / галерея к Н1),
 * НЕ к квартирным лоджиям и балконам (REV-50 follow-up).
 */
export function isFirePathBalconyWidthRule(rule: MinDimensionNormRule): boolean {
  if (rule.metric !== "width") return false;
  if (rule.object !== "балкон" && rule.object !== "лоджия") return false;
  const blob = `${rule.source.quote} ${rule.applicability ?? ""}`.toLowerCase();
  return /түтіндет|незадымл|лестничн|баспалдақ\s+тор|галереялард|типті\s+түтін|типа\s+н\s*1|\bh1\b|ведущ\w*\s+к\s+незадымл/i.test(
    blob
  );
}

/** «В свету» / clear width (СП РК 3.06-101) vs габарит «от стены до ограждения». */
export type WidthMeasurementBasis =
  | "bounding_box"
  | "wall_to_rail"
  | "clear_width";

export function inferWidthMeasurementBasis(
  rule: MinDimensionNormRule | undefined
): WidthMeasurementBasis {
  if (!rule) return "bounding_box";
  const blob = `${rule.source.quote} ${rule.applicability ?? ""}`.toLowerCase();
  if (/в\s+свету|жарықта|clear/i.test(blob)) return "clear_width";
  if (/от\s+наружн|до\s+огражден|сыртқы\s+қабырғадан|қоршауына/i.test(blob)) {
    return "wall_to_rail";
  }
  return "bounding_box";
}

export type MinDimensionsHousingType = "ordinary" | "mgn";

export function filterMinDimensionRulesForHousing(
  rules: MinDimensionNormRule[],
  housingType: MinDimensionsHousingType = "ordinary"
): MinDimensionNormRule[] {
  return rules.filter((rule) => {
    const isSummerRoom =
      rule.object === "балкон" || rule.object === "лоджия";
    const isSummerSize =
      isSummerRoom && (rule.metric === "width" || rule.metric === "depth");
    const mgn = isMgnOrSpecialHousingRule(rule);
    const firePath = isFirePathBalconyWidthRule(rule);

    if (housingType === "ordinary") {
      // Квартирные лоджии/балконы: в СП нет общей мин. ширины для ordinary.
      // п. 4.2.30 — только путь к Н1 → отдельное поле minFirePathOutdoorWidthMm.
      // п. 4.6.5 / 3.06 — только МГН.
      if (isSummerSize) return false;
      return true;
    }

    // housingType === "mgn": МГН 1,4 м; пожарный путь 1,2 м не подменяет п. 4.6.5.
    if (isSummerSize && firePath) return false;
    if (isSummerSize && !mgn && rule.metric === "width" && rule.minValueMm !== 1400) {
      return false;
    }
    return true;
  });
}


export function inferMinDimensionRule(
  sentence: string,
  document: string,
  sourcePdf: string
): MinDimensionNormRule | null {
  const quote = sentence.replace(/\s+/g, " ").trim();
  if (quote.length < 20) return null;

  const minValueMm = parseMinValueMm(quote);
  if (minValueMm === null || minValueMm <= 0) return null;

  const lower = quote.toLowerCase();

  if (
    /простенк|между\s+остеклен|ойықтар\s+арасы|от\s+торца\s+балкон|до\s+оконн/i.test(
      lower
    )
  ) {
    return null;
  }

  const isLoggia = /лодж/i.test(lower);
  const isBalcony = /балкон/i.test(lower);
  if (!isLoggia && !isBalcony) return null;

  const object: MinDimensionObject =
    isBalcony && !isLoggia
      ? "балкон"
      : isLoggia && !isBalcony
        ? "лоджия"
        : "балкон";

  if (/глубин|терең|глубиной/i.test(lower)) {
    if (minValueMm < 1000 || minValueMm > 4000) return null;
    return buildRule(object, "depth", minValueMm, quote, document, sourcePdf);
  }

  if (/ширин|ені|расстояни[ея]\s+от\s+наружн|в\s+свету/i.test(lower)) {
    if (minValueMm < 1000 || minValueMm > 3000) return null;
    return buildRule(object, "width", minValueMm, quote, document, sourcePdf);
  }

  if (/ені|ширин/i.test(lower) && minValueMm >= 1000 && minValueMm <= 2500) {
    return buildRule(object, "width", minValueMm, quote, document, sourcePdf);
  }

  return null;
}

export function inferMinDimensionRules(
  sentence: string,
  document: string,
  sourcePdf: string
): MinDimensionNormRule[] {
  const quote = sentence.replace(/\s+/g, " ").trim();
  const lower = quote.toLowerCase();
  const rules: MinDimensionNormRule[] = [];

  if (
    /простенк|между\s+остеклен|ойықтар\s+арасы|от\s+торца\s+балкон|до\s+оконн/i.test(
      lower
    )
  ) {
    if (/между\s+остеклен|ойықтар\s+арасы|между\s+[^.]{0,40}проем|арасы/i.test(lower)) {
      const betweenMm = pickValueForMetric(quote, "pier_between_openings");
      if (betweenMm !== null && betweenMm >= 1000 && betweenMm <= 3000) {
        rules.push(
          buildRule(
            "противопожарный простенок",
            "pier_between_openings",
            betweenMm,
            quote,
            document,
            sourcePdf
          )
        );
      }
    }
    if (/до\s+оконн|до\s+[^.]{0,30}проем|торца\s+балкон|простенк/i.test(lower)) {
      const toOpeningMm = pickValueForMetric(quote, "pier_to_opening");
      if (toOpeningMm !== null && toOpeningMm >= 800 && toOpeningMm <= 2500) {
        rules.push(
          buildRule(
            "противопожарный простенок",
            "pier_to_opening",
            toOpeningMm,
            quote,
            document,
            sourcePdf
          )
        );
      }
    }
    if (rules.length > 0) return rules;
  }

  const rule = inferMinDimensionRule(sentence, document, sourcePdf);
  if (!rule) return [];

  if (
    rule.metric === "width" &&
    /лодж/i.test(lower) &&
    /балкон/i.test(lower) &&
    rule.object === "балкон"
  ) {
    return [
      rule,
      {
        ...rule,
        id: createHash("sha1")
          .update(`лоджия|${rule.metric}|${rule.source.quote}`)
          .digest("hex")
          .slice(0, 12),
        object: "лоджия",
      },
    ];
  }

  if (
    rule.metric === "depth" &&
    /лодж/i.test(lower) &&
    /балкон/i.test(lower) &&
    rule.object === "балкон"
  ) {
    return [
      rule,
      {
        ...rule,
        id: createHash("sha1")
          .update(`лоджия|${rule.metric}|${rule.source.quote}`)
          .digest("hex")
          .slice(0, 12),
        object: "лоджия",
      },
    ];
  }

  return [rule];
}

function buildRule(
  object: MinDimensionObject,
  metric: MinDimensionMetric,
  minValueMm: number,
  quote: string,
  document: string,
  sourcePdf: string
): MinDimensionNormRule {
  return {
    id: createHash("sha1").update(`${object}|${metric}|${quote}`).digest("hex").slice(0, 12),
    object,
    metric,
    minValueMm,
    source: {
      document,
      clause: extractClause(quote),
      quote,
    },
    sourcePdf,
    applicability: extractApplicability(quote),
  };
}

export function extractMinDimensionRulesFromText(
  text: string,
  document: string,
  sourcePdf: string
): MinDimensionNormRule[] {
  const sentences = text.replace(/\r/g, "").split(/(?<=[.!?;])\s+/);
  const rules: MinDimensionNormRule[] = [];
  const seen = new Set<string>();

  for (const sentence of sentences) {
    if (!/лодж|балкон|простенк|остеклен|ойық|торца/i.test(sentence)) continue;

    for (const rule of inferMinDimensionRules(sentence, document, sourcePdf)) {
      const key = `${rule.object}|${rule.metric}|${rule.minValueMm}|${rule.source.quote}`;
      if (seen.has(key)) continue;
      seen.add(key);
      rules.push(rule);
    }
  }

  return rules;
}

function scoreRule(
  rule: MinDimensionNormRule,
  housingType: MinDimensionsHousingType = "ordinary"
): number {
  let score = 0;
  if (rule.source.clause) score += 10;
  if (/СП\s*РК|SP_RK/i.test(rule.source.document)) score += 30;
  if (/ТР|тех\.?\s*регламент|пожарн/i.test(rule.source.document)) score += 25;
  if (rule.metric === "depth" && rule.minValueMm === 1600) score += 10;
  if (rule.metric === "depth" && rule.minValueMm > 2000) score -= 15;
  if (/общие\s+для\s+жилой\s+группы/i.test(rule.source.quote)) score -= 20;
  if (rule.metric === "pier_to_opening" && rule.minValueMm === 1200) score += 20;
  if (rule.metric === "pier_between_openings" && rule.minValueMm === 1600) score += 25;
  if (rule.metric === "pier_between_openings" && rule.minValueMm === 1200) score += 5;

  const mgn = isMgnOrSpecialHousingRule(rule);
  const firePath = isFirePathBalconyWidthRule(rule);

  if (housingType === "ordinary") {
    if (mgn) score -= 100;
    if (firePath) score -= 50;
  }

  if (housingType === "mgn" && mgn && rule.metric === "width" && rule.minValueMm === 1400) {
    score += 40;
  }
  if (housingType === "mgn" && mgn && rule.metric === "depth" && rule.minValueMm === 1600) {
    score += 30;
  }

  return score;
}

export function pickPrimaryMinDimensionRules(
  rules: MinDimensionNormRule[],
  options?: { housingType?: MinDimensionsHousingType }
): Partial<Record<`${MinDimensionObject}:${MinDimensionMetric}`, MinDimensionNormRule>> {
  const housingType = options?.housingType ?? "ordinary";
  const filtered = filterMinDimensionRulesForHousing(rules, housingType);
  const buckets = new Map<string, MinDimensionNormRule[]>();

  for (const rule of filtered) {
    const key = `${rule.object}:${rule.metric}`;
    const list = buckets.get(key) ?? [];
    list.push(rule);
    buckets.set(key, list);
  }

  const result: Partial<
    Record<`${MinDimensionObject}:${MinDimensionMetric}`, MinDimensionNormRule>
  > = {};
  for (const [key, list] of buckets) {
    const best = [...list].sort(
      (a, b) => scoreRule(b, housingType) - scoreRule(a, housingType)
    )[0];
    result[key as `${MinDimensionObject}:${MinDimensionMetric}`] = best;
  }
  return result;
}

export interface ResolvedMinDimensionLimits {
  minBalconyWidthMm?: number;
  minLoggiaWidthMm?: number;
  minLoggiaDepthMm?: number;
  /** п. 4.2.30 — только воздушная зона / галерея к Н1, не квартирные лоджии. */
  minFirePathOutdoorWidthMm?: number;
  minFirePierToOpeningMm?: number;
  minFirePierBetweenOpeningsMm?: number;
  appliedRules: MinDimensionNormRule[];
  housingType: MinDimensionsHousingType;
  /** How width is measured in Revit vs the cited norm. */
  widthMeasurementBasis: WidthMeasurementBasis;
  /**
   * v1 compares bounding-box footprint (smaller span) to the limit.
   * True «в свету» / clear width is not computed separately yet.
   */
  measurementNote: string;
  skippedMgnRules: number;
  /** How many fire-path (п. 4.2.30) width rules were kept for Н1-path only. */
  firePathWidthRules: number;
}

export function resolveMinDimensionLimits(
  rules: MinDimensionNormRule[],
  overrides?: Partial<ResolvedMinDimensionLimits> & {
    housingType?: MinDimensionsHousingType;
  }
): ResolvedMinDimensionLimits {
  const housingType = overrides?.housingType ?? "ordinary";
  const skippedMgnRules =
    housingType === "ordinary"
      ? rules.filter(
          (r) =>
            isMgnOrSpecialHousingRule(r) &&
            (r.object === "балкон" || r.object === "лоджия") &&
            (r.metric === "width" || r.metric === "depth")
        ).length
      : 0;

  const firePathRules = rules
    .filter((r) => isFirePathBalconyWidthRule(r))
    .sort((a, b) => scoreRule(b, "ordinary") - scoreRule(a, "ordinary"));
  // Prefer 1200 mm п. 4.2.30 among fire-path rules.
  const bestFirePath =
    firePathRules.find((r) => r.minValueMm === 1200) ?? firePathRules[0];

  const primary = pickPrimaryMinDimensionRules(rules, { housingType });
  const applied: MinDimensionNormRule[] = [];

  const pick = (
    object: MinDimensionObject,
    metric: MinDimensionMetric,
    overrideKey: keyof ResolvedMinDimensionLimits
  ): number | undefined => {
    const override = overrides?.[overrideKey];
    if (typeof override === "number" && override > 0) return override;
    const rule = primary[`${object}:${metric}`];
    if (rule) {
      applied.push(rule);
      return rule.minValueMm;
    }
    return undefined;
  };

  const minBalconyWidthMm = pick("балкон", "width", "minBalconyWidthMm");
  const minLoggiaWidthMm = pick("лоджия", "width", "minLoggiaWidthMm");

  let minFirePathOutdoorWidthMm: number | undefined =
    typeof overrides?.minFirePathOutdoorWidthMm === "number" &&
    overrides.minFirePathOutdoorWidthMm > 0
      ? overrides.minFirePathOutdoorWidthMm
      : undefined;
  if (minFirePathOutdoorWidthMm === undefined && bestFirePath) {
    minFirePathOutdoorWidthMm = bestFirePath.minValueMm;
    applied.push(bestFirePath);
  }

  const widthRule =
    primary["балкон:width"] ??
    primary["лоджия:width"] ??
    bestFirePath ??
    applied.find((r) => r.metric === "width");
  const widthMeasurementBasis = inferWidthMeasurementBasis(widthRule);

  return {
    minBalconyWidthMm,
    minLoggiaWidthMm,
    minLoggiaDepthMm: pick("лоджия", "depth", "minLoggiaDepthMm"),
    minFirePathOutdoorWidthMm,
    minFirePierToOpeningMm: pick(
      "противопожарный простенок",
      "pier_to_opening",
      "minFirePierToOpeningMm"
    ),
    minFirePierBetweenOpeningsMm: pick(
      "противопожарный простенок",
      "pier_between_openings",
      "minFirePierBetweenOpeningsMm"
    ),
    appliedRules: applied,
    housingType,
    widthMeasurementBasis,
    measurementNote:
      housingType === "ordinary"
        ? "Обычное жильё: мин. ширина квартирных лоджий/балконов по СП не нормируется " +
          "(п. 4.6.5 — только МГН). п. 4.2.30 (1,2 м) — только воздушная зона / путь к Н1. " +
          "Факт — меньший пролёт bounding box; «в свету» отдельно не считается."
        : "МГН: ширина лоджий/балконов ≥ 1,4 м (п. 4.6.5 / 3.06-101). " +
          "Факт — bounding box; норма может быть «в свету» / от стены до ограждения.",
    skippedMgnRules,
    firePathWidthRules: firePathRules.length,
  };
}

export async function loadMinDimensionRulesFromNormatives(options?: {
  normativesDir?: string;
  pdfFiles?: string[];
  scanAllPdfs?: boolean;
}): Promise<{
  rules: MinDimensionNormRule[];
  warnings: string[];
  normativesDir: string;
}> {
  const normativesDir = options?.normativesDir ?? (await resolveNormativesDir());
  let pdfFiles = options?.pdfFiles ?? [...DEFAULT_MIN_DIMENSIONS_PDF_FILES];

  if (options?.scanAllPdfs) {
    pdfFiles = (await readdir(normativesDir)).filter((file) =>
      file.toLowerCase().endsWith(".pdf")
    );
  }

  const rules: MinDimensionNormRule[] = [];
  const warnings: string[] = [];

  for (const fileName of pdfFiles) {
    const pdfPath = join(normativesDir, fileName);
    try {
      const pdfBuffer = await readFile(pdfPath);
      const parsedPdf = await pdfParse(pdfBuffer);
      const document = normalizeDocumentName(fileName);
      const extracted = extractMinDimensionRulesFromText(
        parsedPdf.text,
        document,
        fileName
      );
      rules.push(...extracted);
      if (extracted.length === 0) {
        warnings.push(`В ${fileName} не найдено требований к лоджиям/балконам/простенкам.`);
      }
    } catch (error) {
      warnings.push(
        `Не удалось прочитать ${fileName}: ${
          error instanceof Error ? error.message : String(error)
        }`
      );
    }
  }

  const deduped = [...new Map(rules.map((r) => [r.id, r])).values()];
  if (deduped.length === 0) {
    warnings.push(
      `В каталоге normatives не извлечено ни одного правила по минимальным размерам (${normativesDir}).`
    );
  }

  return { rules: deduped, warnings, normativesDir };
}
