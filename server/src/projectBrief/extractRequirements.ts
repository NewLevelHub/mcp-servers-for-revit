import { createHash } from "node:crypto";
import {
  type BriefExtractionInput,
  type BriefExtractionResult,
  type BriefRequirement,
  type BriefRequirementType,
  type BriefRequirementUnit,
  briefExtractionInputSchema,
  briefRequirementSchema,
} from "./types.js";

/**
 * REV-182 heuristic extractor for project brief documents (ТЗ, задание на
 * проектирование, протоколы совещаний) — same shape of job as
 * normatives/extractRules.ts, but ТЗ prose is much less structured than a
 * numbered normative code (no reliable "п. 4.2.3" clause numbering to split
 * on, room-programme tables that pdf-parse flattens unpredictably). Built and
 * unit-tested against synthetic examples only — no real project ТЗ was
 * available to ground it against (unlike normatives, and unlike REV-179/180/181's
 * live-model checks). Treat every pattern here as a starting set: extend it
 * the moment a real document shows a phrasing this misses, per the ticket's own
 * rule — "нет требования в документе → честное 'не нашёл', а не догадка". A
 * false negative (nothing extracted) is the safe failure; a false positive
 * (wrong count attributed to the wrong room type) is the one to guard against,
 * so every pattern below requires an explicit, unambiguous cue ("шт", "не
 * менее", "количество") rather than guessing from bare proximity.
 */

// JS \w is ASCII-only (same gotcha normatives/extractRules.ts documents) — a
// Cyrillic suffix needs an explicit character class, not \w*, or it silently
// stops matching one letter into the word (caught by extractRequirements.test.ts:
// "комнатн\w*" against "комнатных" matched nothing, because \w* consumed zero of
// "ых" and the required trailing \s+ then failed on the next Cyrillic letter).
const CYR_SUFFIX = "[а-яё]*";
const DIGIT_APARTMENT_RE = new RegExp(
  `(\\d)\\s*[-–]?\\s*комнатн${CYR_SUFFIX}\\s+квартир${CYR_SUFFIX}`,
  "i"
);
const WORD_APARTMENT_RE = new RegExp(
  `(одно|двух|трех|трёх|четырех|четырёх|пяти|шести)\\s*комнатн${CYR_SUFFIX}\\s+квартир${CYR_SUFFIX}`,
  "i"
);
const WORD_TO_DIGIT: Record<string, string> = {
  одно: "1",
  двух: "2",
  трех: "3",
  трёх: "3",
  четырех: "4",
  четырёх: "4",
  пяти: "5",
  шести: "6",
};

const STATIC_OBJECT_PATTERNS: Array<{ pattern: RegExp; object: string }> = [
  { pattern: /студи/i, object: "студия" },
  { pattern: /пентхаус/i, object: "пентхаус" },
  { pattern: /машиномест/i, object: "машиноместо" },
  { pattern: /паркинг|автостоянк/i, object: "паркинг" },
  { pattern: /кладов/i, object: "кладовая" },
  { pattern: /офис/i, object: "офис" },
  { pattern: new RegExp(`магазин|торгов${CYR_SUFFIX}\\s+помещен`, "i"), object: "торговое помещение" },
  { pattern: /квартир/i, object: "квартира" },
];

/** First matching object wins, most specific patterns (with a digit) checked first. */
function inferObject(text: string): string | null {
  const digitMatch = text.match(DIGIT_APARTMENT_RE);
  if (digitMatch) return `${digitMatch[1]}-комнатная квартира`;

  const wordMatch = text.match(WORD_APARTMENT_RE);
  if (wordMatch) {
    const digit = WORD_TO_DIGIT[wordMatch[1].toLowerCase()];
    if (digit) return `${digit}-комнатная квартира`;
  }

  for (const { pattern, object } of STATIC_OBJECT_PATTERNS) {
    if (pattern.test(text)) return object;
  }
  return null;
}

// "шт"/"штук" next to a number is an unambiguous count cue, independent of word
// order — covers both "Студия — 25 шт." and "25 шт. студий".
const COUNT_WITH_UNITS_RE = /(\d+)\s*(?:шт\.?|штук)/i;
// Explicit count-labelling words without "шт" (tables sometimes omit it).
const COUNT_LABEL_RE =
  /(?:не\s+менее|количество|кол-во|итого|всего)\s*[:\-–—]?\s*(\d+)(?!\s*(?:м²|м2|кв\.?\s*м|мм|м\b))/i;

function parseCount(text: string): number | null {
  const withUnits = text.match(COUNT_WITH_UNITS_RE);
  if (withUnits) return Number(withUnits[1]);

  const labeled = text.match(COUNT_LABEL_RE);
  if (labeled) return Number(labeled[1]);

  return null;
}

const AREA_MIN_RE = /не\s+менее\s+(\d+(?:[.,]\d+)?)\s*(?:м²|м2|кв\.?\s*м)/i;

function parseAreaMin(text: string): number | null {
  const match = text.match(AREA_MIN_RE);
  if (!match) return null;
  return Number(match[1].replace(",", "."));
}

// Stems only, no \w* suffix needed for a .test() boolean check — but the stems
// themselves must be short enough to actually be prefixes of every conjugated
// form. "должн" is not a prefix of "должен" (о vs е), and "предусмотр" is not a
// prefix of "предусматривать" (о/а aspectual alternation) — both caught by
// extractRequirements.test.ts. "предусм" covers both предусмотр-/предусматр-.
const QUALITATIVE_RE = /долж[а-яё]*|следует|необходим[а-яё]*|предусм[а-яё]*|требуется|обязан[а-яё]*/i;

function inferQualitative(text: string): BriefRequirementType | null {
  return QUALITATIVE_RE.test(text) ? "requirement" : null;
}

function normalizeWhitespace(text: string): string {
  return text.replace(/\s+/g, " ").trim();
}

interface Segment {
  clause: string;
  text: string;
}

const CLAUSE_PREFIX_RE = /^(?:п\.?|пункт|раздел|§)\s*(\d+(?:\.\d+)*)\s*[.:)\-–—]?\s*/i;
const CLAUSE_NUMBER_RE = /^(\d+(?:\.\d+)*)\s+\p{Lu}/u;

/**
 * One non-empty line = one segment. ТЗ prose rarely carries reliable clause
 * numbering throughout the way a normative code does (normatives'
 * splitIntoSegments assumes exactly that), so this deliberately does not try
 * to merge continuation lines into a running clause the way that module does —
 * a wrong merge would attach one room count to the wrong paragraph's quote.
 * A line that does start with a clause/section number is tagged with it;
 * everything else keeps clause "" and still carries its own quote for citation.
 */
export function splitIntoSegments(text: string): Segment[] {
  return text
    .split(/\r?\n/)
    .map((line) => normalizeWhitespace(line))
    .filter(Boolean)
    .map((line) => {
      const prefixed = line.match(CLAUSE_PREFIX_RE);
      if (prefixed?.[1]) return { clause: prefixed[1], text: line };
      const numbered = line.match(CLAUSE_NUMBER_RE);
      if (numbered?.[1]) return { clause: numbered[1], text: line };
      return { clause: "", text: line };
    });
}

function stableId(parts: string[]): string {
  return createHash("sha256").update(parts.join("|")).digest("hex").slice(0, 16);
}

function buildRequirement(
  segment: Segment,
  document: string,
  page: number | undefined,
  type: BriefRequirementType,
  object: string,
  value: number | string,
  unit: BriefRequirementUnit
): BriefRequirement {
  const requirement: BriefRequirement = {
    id: stableId([document, segment.clause, segment.text, type, object]),
    type,
    object,
    value,
    unit,
    source: {
      document,
      clause: segment.clause,
      quote: segment.text,
      page,
    },
  };
  return briefRequirementSchema.parse(requirement);
}

function defaultDocumentTitle(explicit?: string): string {
  return explicit?.trim() || "проектное задание";
}

/**
 * Convert already-extracted plain text into structured requirements. Does not
 * parse PDF/DOCX bytes itself — see extractRequirementsFromFile.ts.
 */
export function extractRequirementsFromText(
  input: BriefExtractionInput
): BriefExtractionResult {
  const parsed = briefExtractionInputSchema.parse(input);
  const document = defaultDocumentTitle(parsed.document);
  const warnings: string[] = [];
  const requirements: BriefRequirement[] = [];

  const segments = splitIntoSegments(parsed.text).map((segment) =>
    parsed.clauseHint && !segment.clause ? { ...segment, clause: parsed.clauseHint } : segment
  );

  for (const segment of segments) {
    const object = inferObject(segment.text);
    const areaMin = parseAreaMin(segment.text);
    const count = object ? parseCount(segment.text) : null;

    let matched = false;

    if (object && areaMin != null) {
      requirements.push(
        buildRequirement(segment, document, parsed.page, "room_area_min", object, areaMin, "m2")
      );
      matched = true;
    }

    if (object && count != null) {
      requirements.push(
        buildRequirement(segment, document, parsed.page, "room_count", object, count, "pcs")
      );
      matched = true;
    }

    if (!matched) {
      const qualitative = inferQualitative(segment.text);
      if (qualitative) {
        requirements.push(
          buildRequirement(segment, document, parsed.page, qualitative, object ?? "объект", segment.text, "none")
        );
        matched = true;
      }
    }

    if (!matched && /\d/.test(segment.text) && segment.text.length > 3) {
      warnings.push(`Строка с числом не распознана как требование: ${segment.text}`);
    }
  }

  return { requirements, warnings };
}
