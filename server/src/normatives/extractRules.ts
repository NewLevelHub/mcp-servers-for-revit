import { createHash } from "node:crypto";
import {
  type NormativeApplicability,
  type NormativeExtractionInput,
  type NormativeExtractionMetadata,
  type NormativeExtractionResult,
  type NormativeNumericRange,
  type NormativeRule,
  type NormativeRuleType,
  type NormativeUnit,
  normativeExtractionInputSchema,
  normativeRuleSchema,
} from "./types.js";

const CLAUSE_PREFIX_RE =
  /^(?:п\.?|пункт|§|ст\.?|статья)\s*(\d+(?:\.\d+)*)\s*[.:)\-–—]?\s*/i;

const CLAUSE_NUMBER_RE = /^(\d+(?:\.\d+)*)\s+\p{Lu}/u;

const NUMERIC_VALUE_RE = /(\d+(?:[.,]\d+)?)/;

const UNIT_ALIASES: Record<string, NormativeUnit> = {
  мм: "mm",
  mm: "mm",
  м: "m",
  m: "m",
  "м²": "m2",
  "м2": "m2",
  "кв.м": "m2",
  "кв. м": "m2",
  "кв м": "m2",
  "м³": "m3",
  "м3": "m3",
  "%": "percent",
  шт: "pcs",
};

/** Cyrillic word suffix — JS \\w is ASCII-only. */
const CYR_SUFFIX = "[а-яёa-z]*";

const OBJECT_PATTERNS: Array<{ pattern: RegExp; object: string }> = [
  {
    pattern: new RegExp(`эвакуационн${CYR_SUFFIX}\\s+коридор`, "i"),
    object: "эвакуационный коридор",
  },
  { pattern: /коридор/i, object: "коридор" },
  { pattern: /дәліз/i, object: "коридор" },
  { pattern: /эвакуаци(?:ялық|онн\w*)\s+дәліз/i, object: "эвакуационный коридор" },
  { pattern: /лодж/i, object: "лоджия" },
  { pattern: /балкон/i, object: "балкон" },
  { pattern: /простенок/i, object: "противопожарный простенок" },
  { pattern: /двер/i, object: "дверь" },
  { pattern: /есік/i, object: "дверь" },
  { pattern: /проем/i, object: "проём" },
  { pattern: /жил(?:ое|ого|ые|ых|ым)?\s+помещен/i, object: "жилое помещение" },
  { pattern: /тұрғын\s+үй-?жай/i, object: "жилое помещение" },
  { pattern: /комнат/i, object: "комната" },
  { pattern: /бөлме/i, object: "комната" },
  { pattern: /квартир/i, object: "квартира" },
  { pattern: /пәтер/i, object: "квартира" },
  { pattern: /санузел|ванн(?:ая|ой)|туалет/i, object: "санузел" },
  {
    pattern: new RegExp(`основн${CYR_SUFFIX}\\s+надпис`, "i"),
    object: "основная надпись",
  },
  { pattern: /формат\s+(?:листа|чертежа)/i, object: "формат листа" },
  { pattern: /высот[аы]\s+строк/i, object: "высота строки" },
  { pattern: /площад/i, object: "площадь" },
  { pattern: /алаң|ауданы/i, object: "площадь" },
  { pattern: /ширин/i, object: "ширина" },
  { pattern: /ені/i, object: "ширина" },
  { pattern: /глубин/i, object: "глубина" },
  { pattern: /высот/i, object: "высота" },
  { pattern: /биікт/i, object: "высота" },
];

// JS \b is ASCII-only — use explicit boundaries for Cyrillic norm text.
const APPLICABILITY_PATTERNS: RegExp[] = [
  /(?:^|[\s(,])для\s+([^;]+)/i,
  /(?:^|[\s(,])в\s+жилых(?:\s+зданиях)?/i,
  /(?:^|[\s(,])в\s+зданиях\s+([^;]+)/i,
  /(?:^|[\s(,])при\s+([^;]+)/i,
  /(?:^|[\s(,])в\s+случае\s+([^;]+)/i,
  /(?:^|[\s(,])үшін\s+([^;]+)/i,
  /(?:^|[\s(,])тұрғын\s+([^;]+)/i,
  /(?:^|[\s(,])ғимарат(?:тар)?(?:да|да)?\s+([^;]+)/i,
];

interface ParsedLimit {
  type: NormativeRuleType;
  value: number | { min?: number; max?: number };
  unit: NormativeUnit;
}

interface Segment {
  clause: string;
  text: string;
}

function stableRuleId(parts: string[]): string {
  return createHash("sha256").update(parts.join("|")).digest("hex").slice(0, 16);
}

function normalizeWhitespace(text: string): string {
  return text.replace(/\s+/g, " ").trim();
}

function parseNumber(raw: string): number {
  return Number(raw.replace(/\s/g, "").replace(",", "."));
}

function parseUnit(raw: string | undefined): NormativeUnit {
  if (!raw) return "none";
  const key = raw.trim().toLowerCase();
  return UNIT_ALIASES[key] ?? "none";
}

function toBaseUnit(value: number, unit: NormativeUnit): NormativeNumericRange {
  switch (unit) {
    case "mm":
      return { exact: value };
    case "m":
      return { exact: value * 1000 };
    case "m2":
      return { exact: value };
    case "m3":
      return { exact: value };
    case "percent":
      return { exact: value };
    default:
      return { exact: value };
  }
}

function rangeToNormalized(
  value: number | { min?: number; max?: number },
  unit: NormativeUnit
): NormativeNumericRange | undefined {
  if (typeof value === "number") {
    return toBaseUnit(value, unit);
  }

  const normalized: NormativeNumericRange = {};
  if (value.min !== undefined) {
    normalized.min = toBaseUnit(value.min, unit).exact;
  }
  if (value.max !== undefined) {
    normalized.max = toBaseUnit(value.max, unit).exact;
  }
  return Object.keys(normalized).length > 0 ? normalized : undefined;
}

function inferObject(text: string): string {
  for (const { pattern, object } of OBJECT_PATTERNS) {
    if (pattern.test(text)) return object;
  }
  return "объект";
}

function trimApplicabilityPhrase(phrase: string): string {
  return normalizeWhitespace(phrase).replace(/[.,]+$/, "");
}

function parseApplicability(text: string): NormativeApplicability | null {
  for (const pattern of APPLICABILITY_PATTERNS) {
    const match = text.match(pattern);
    if (!match) continue;

    const raw = trimApplicabilityPhrase(match[0]);
    const tail = trimApplicabilityPhrase(match[1] ?? "");
    const applicability: NormativeApplicability = { raw };

    if (/жил|тұрғын/i.test(tail) || /жил|тұрғын/i.test(raw)) {
      applicability.roomType = "жилые помещения";
    }
    if (/обществен|қоғамдық/i.test(tail)) {
      applicability.buildingType = "общественные здания";
    }
    if (/класс[а-яё]*\s*ф|сынып\s*ф/i.test(tail) || /ф\s*1[.,]\d/i.test(tail)) {
      applicability.conditions = [tail || raw];
    }

    return applicability;
  }
  return null;
}

function extractClauseNumber(line: string, fallback?: string): string {
  const prefixed = line.match(CLAUSE_PREFIX_RE);
  if (prefixed?.[1]) return prefixed[1];

  const numbered = line.match(CLAUSE_NUMBER_RE);
  if (numbered?.[1]) return numbered[1];

  return fallback ?? "";
}

function stripClausePrefix(line: string): string {
  let result = line.replace(CLAUSE_PREFIX_RE, "");
  const numbered = result.match(/^(\d+(?:\.\d+)*)\s+/);
  if (numbered) {
    result = result.slice(numbered[0].length);
  }
  return normalizeWhitespace(result);
}

function splitIntoSegments(text: string, clauseHint?: string): Segment[] {
  const lines = text
    .split(/\r?\n/)
    .map((line) => normalizeWhitespace(line))
    .filter(Boolean);

  const segments: Segment[] = [];
  let currentClause = clauseHint ?? "";
  let buffer: string[] = [];

  const flush = () => {
    if (buffer.length === 0) return;
    segments.push({
      clause: currentClause,
      text: buffer.join(" "),
    });
    buffer = [];
  };

  for (const line of lines) {
    const clause = extractClauseNumber(line);
    const startsNewClause = Boolean(clause) && clause !== currentClause;

    if (startsNewClause) {
      flush();
      currentClause = clause;
      buffer.push(stripClausePrefix(line));
      continue;
    }

    if (!currentClause) {
      currentClause = extractClauseNumber(line, clauseHint);
    }

    buffer.push(stripClausePrefix(line));
  }

  flush();

  if (segments.length === 0) {
    segments.push({
      clause: clauseHint ?? "",
      text: normalizeWhitespace(text),
    });
  }

  return segments;
}

/** Longer unit tokens first so `м` does not consume the start of `м²` / `м³`. */
const UNIT_CAPTURE = "мм|mm|кв\\.?\\s*м|м²|м2|м³|м3|м|m|%|шт";

function parseLimits(text: string): ParsedLimit[] {
  const limits: ParsedLimit[] = [];
  const rangePattern = new RegExp(
    `(?:от|бастап)\\s+(\\d+(?:[.,]\\d+)?)\\s*(${UNIT_CAPTURE})?\\s*(?:до|дейін)\\s+(\\d+(?:[.,]\\d+)?)\\s*(${UNIT_CAPTURE})?`,
    "gi"
  );
  const minPattern = new RegExp(
    `(?:не\\s+менее|кемінде)\\s+(\\d+(?:[.,]\\d+)?)\\s*(${UNIT_CAPTURE})?`,
    "gi"
  );
  const maxPattern = new RegExp(
    `(?:не\\s+более|аспауға\\s+тиіс|артық\\s+емес|жоғары\\s+емес)\\s+(\\d+(?:[.,]\\d+)?)\\s*(${UNIT_CAPTURE})?`,
    "gi"
  );
  const exactPattern = new RegExp(
    `(?:равн(?:а|ы)?|составляет|тең|құрайды)\\s+(\\d+(?:[.,]\\d+)?)\\s*(${UNIT_CAPTURE})?`,
    "gi"
  );

  for (const match of text.matchAll(rangePattern)) {
    limits.push({
      type: "range",
      value: {
        min: parseNumber(match[1]),
        max: parseNumber(match[3]),
      },
      unit: parseUnit(match[2] ?? match[4]),
    });
  }

  if (limits.length > 0) return limits;

  for (const match of text.matchAll(minPattern)) {
    limits.push({
      type: "min_value",
      value: parseNumber(match[1]),
      unit: parseUnit(match[2]),
    });
  }

  for (const match of text.matchAll(maxPattern)) {
    limits.push({
      type: "max_value",
      value: parseNumber(match[1]),
      unit: parseUnit(match[2]),
    });
  }

  for (const match of text.matchAll(exactPattern)) {
    limits.push({
      type: "exact_value",
      value: parseNumber(match[1]),
      unit: parseUnit(match[2]),
    });
  }

  return limits;
}

function inferQualitativeRule(text: string): NormativeRuleType | null {
  if (/не\s+допускается|запрещ|рұқсат\s+етілмейді|тыйым\s+салынады/i.test(text)) {
    return "prohibition";
  }
  if (/должн|следует|необходим|предусматрив|болуы\s+тиіс|көзделеді/i.test(text)) {
    return "requirement";
  }
  return null;
}

function defaultDocumentTitle(text: string, explicit?: string): string {
  if (explicit) return explicit;

  const gost = text.match(/ГОСТ\s*[\d\.\-–—]+(?:\s*[-–—]\s*\d+)?/i);
  if (gost) return normalizeWhitespace(gost[0]);

  const sp = text.match(/СП\s*РК\s*[\d\.\-–—]+/i);
  if (sp) return normalizeWhitespace(sp[0]);

  const sn = text.match(/СН\s*РК\s*[\d\.\-–—]+/i);
  if (sn) return normalizeWhitespace(sn[0]);

  return "нормативный документ";
}

function buildRule(
  segment: Segment,
  document: string,
  page: number | undefined,
  limit: ParsedLimit | null,
  qualitativeType: NormativeRuleType | null
): NormativeRule | null {
  const text = segment.text;
  if (!text) return null;

  const type = limit?.type ?? qualitativeType ?? "note";
  const unit = limit?.unit ?? "none";
  const value = limit?.value ?? text;

  const rule: NormativeRule = {
    id: stableRuleId([document, segment.clause, text, type]),
    type,
    object: inferObject(text),
    value,
    unit,
    applicability: parseApplicability(text),
    source: {
      document,
      clause: segment.clause ? `п. ${segment.clause}` : "",
      quote: text,
      page,
    },
    normalized:
      limit && unit !== "none"
        ? rangeToNormalized(limit.value, unit)
        : undefined,
  };

  return normativeRuleSchema.parse(rule);
}

/**
 * Convert plain normative text (from Cursor-read PDF) into structured rules.
 * Does not parse PDF bytes — pass already extracted text only.
 */
export function extractRulesFromText(
  input: NormativeExtractionInput
): NormativeExtractionResult {
  const parsedInput = normativeExtractionInputSchema.parse(input);
  const warnings: string[] = [];
  const rules: NormativeRule[] = [];

  const metadata: NormativeExtractionMetadata = {
    mode: parsedInput.metadata?.mode ?? "embedded-text",
    confidence: parsedInput.metadata?.confidence,
    extractedAt: parsedInput.metadata?.extractedAt ?? new Date().toISOString(),
  };

  if (metadata.mode === "ocr" && metadata.confidence === undefined) {
    warnings.push(
      "OCR mode is experimental: provide metadata.confidence when available."
    );
  }

  const document = defaultDocumentTitle(parsedInput.text, parsedInput.document);
  const segments = splitIntoSegments(parsedInput.text, parsedInput.clauseHint);

  for (const segment of segments) {
    const limits = parseLimits(segment.text);
    const qualitativeType = inferQualitativeRule(segment.text);

    if (limits.length === 0 && !qualitativeType) {
      if (NUMERIC_VALUE_RE.test(segment.text)) {
        warnings.push(
          `Не удалось распознать единицу или тип ограничения: ${segment.text}`
        );
      }
      continue;
    }

    if (limits.length === 0) {
      const rule = buildRule(
        segment,
        document,
        parsedInput.page,
        null,
        qualitativeType
      );
      if (rule) rules.push(rule);
      continue;
    }

    for (const limit of limits) {
      const rule = buildRule(
        segment,
        document,
        parsedInput.page,
        limit,
        qualitativeType
      );
      if (rule) rules.push(rule);
    }
  }

  return {
    rules,
    warnings,
    metadata,
  };
}

/** Sample excerpts for manual / automated smoke checks (GOST & SP RK). */
export const NORMATIVE_SAMPLE_TEXTS = {
  gost2110197: {
    document: "ГОСТ 21.101-97",
    text: `
5.1.4 Основная надпись выполняется с высотой строки не менее 5 мм.
5.2.1 Формат листа для чертежей основного комплекта должен быть не менее 297 мм по короткой стороне.
`,
  },
  spRk302101: {
    document: "СП РК 3.02-101",
    text: `
4.2.3 Ширина коридора в жилых зданиях должна быть не менее 1,2 м при двустороннем расположении дверей.
5.1.2 Площадь жилого помещения (комнаты) в квартире должна быть не менее 9 м².
6.3.1 Ширина эвакуационного коридора должна быть не менее 1200 мм для зданий класса Ф1.1.
`,
  },
} as const;
