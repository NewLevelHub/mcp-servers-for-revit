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

/** Same pilot PDF set as fire-door checks — SP/SN RK fire-safety norms. */
export const DEFAULT_EVACUATION_WIDTH_PDF_FILES = DEFAULT_FIRE_DOOR_PDF_FILES;

export type EvacuationWidthObject =
  | "эвакуационный коридор"
  | "коридор"
  | "тамбур"
  | "проход";

export interface EvacuationWidthNormRule {
  id: string;
  object: EvacuationWidthObject;
  minWidthMm: number;
  source: NormativeSourceRef;
  sourcePdf: string;
  applicability?: string;
}

const CLAUSE_PREFIX_RE =
  /(?:^|\s)(?:п\.?\s*|§\s*)(\d+(?:\.\d+)*)\s*[.:)\-–—]?\s*/i;

const CYR_SUFFIX = "[а-яёa-z]*";

const WIDTH_CONTEXT_RE =
  /(?:ширин[а-яё]*|ені|width)|(?:эвакуационн[а-яё]*\s+коридор|коридор[а-яё]*|corridor|дәліз|тамбур|проход[а-яё]*|hall)/i;

const MIN_WIDTH_RE =
  /(?:не\s+менее|кем\s+емес|не\s+должн[а-яё]*\s+быть\s+менее|≥|>=)\s*(\d[\d\s,.]*)\s*(мм|mm|м(?:\s|\.|,|$)|m(?:\s|\.|,|$))/i;

/** Ventilation / air-exchange tables often contain «60 м³/ч» near the word «коридор». */
const NON_WIDTH_CONTEXT_RE =
  /м\s*3\s*\/?\s*ч|м³\/ч|воздухообмен|кратност|вытяжн|приток|температур|конфорочн|плит[а-яё]*/i;

const MIN_PLAUSIBLE_CORRIDOR_WIDTH_MM = 800;
const MAX_PLAUSIBLE_CORRIDOR_WIDTH_MM = 6000;

function parseNumber(raw: string): number {
  return Number(raw.replace(/\s/g, "").replace(",", "."));
}

function extractClause(sentence: string): string {
  const match = sentence.match(CLAUSE_PREFIX_RE);
  return match ? `п. ${match[1]}` : "";
}

function parseMinWidthMm(sentence: string): number | null {
  if (NON_WIDTH_CONTEXT_RE.test(sentence)) return null;

  const match = sentence.match(MIN_WIDTH_RE);
  if (!match) {
    const looseM = sentence.match(/(\d+[,.]\d+)\s*м\b/i);
    if (looseM && /ширин|коридор|дәліз/i.test(sentence)) {
      const after = sentence.slice((looseM.index ?? 0) + looseM[0].length);
      if (/^\s*3|³/.test(after)) return null;
      return parseNumber(looseM[1]) * 1000;
    }
    return null;
  }

  const value = parseNumber(match[1]);
  const unit = match[2].toLowerCase();
  if (unit === "мм" || unit === "mm") {
    return value;
  }
  if (unit.startsWith("м") || unit.startsWith("m")) {
    const after = sentence.slice(match.index! + match[0].length);
    if (/^\s*3|³/.test(after)) return null;
    return value * 1000;
  }
  return value;
}

export function isPlausibleCorridorWidthRule(
  sentence: string,
  minWidthMm: number
): boolean {
  if (NON_WIDTH_CONTEXT_RE.test(sentence)) return false;
  if (!/ширин|ені|width/i.test(sentence)) return false;
  if (minWidthMm < MIN_PLAUSIBLE_CORRIDOR_WIDTH_MM) return false;
  if (minWidthMm > MAX_PLAUSIBLE_CORRIDOR_WIDTH_MM) return false;
  return true;
}

export function inferEvacuationWidthObject(sentence: string): EvacuationWidthObject {
  const normalized = sentence.toLowerCase();
  if (
    new RegExp(`эвакуационн${CYR_SUFFIX}\\s+коридор`, "i").test(normalized) ||
    /эвакуаци[а-яё]*\s+дәліз/i.test(normalized)
  ) {
    return "эвакуационный коридор";
  }
  if (/тамбур/i.test(normalized)) {
    return "тамбур";
  }
  if (/коридор|corridor|дәліз|hall/i.test(normalized)) {
    return "коридор";
  }
  return "проход";
}

function extractApplicability(sentence: string): string | undefined {
  const match = sentence.match(
    /(?:для\s+зданий\s+[^.;]+|класс[а-яё]*\s*ф[^.;]+|в\s+жилых\s+зданиях[^.;]*|при\s+[^.;]+)/i
  );
  return match ? match[0].replace(/\s+/g, " ").trim() : undefined;
}

export function extractEvacuationWidthRulesFromText(
  text: string,
  document: string,
  sourcePdf: string
): EvacuationWidthNormRule[] {
  const sentences = text.replace(/\r/g, "").split(/(?<=[.!?])\s+/);
  const rules: EvacuationWidthNormRule[] = [];
  const seen = new Set<string>();

  for (const sentence of sentences) {
    if (!/ширин|ені|width/i.test(sentence)) continue;
    if (!WIDTH_CONTEXT_RE.test(sentence)) continue;

    const minWidthMm = parseMinWidthMm(sentence);
    if (minWidthMm === null || minWidthMm <= 0) continue;
    if (!isPlausibleCorridorWidthRule(sentence, minWidthMm)) continue;

    const quote = sentence.replace(/\s+/g, " ").trim();
    if (quote.length < 25 || seen.has(quote)) continue;
    seen.add(quote);

    rules.push({
      id: createHash("sha1").update(quote).digest("hex").slice(0, 12),
      object: inferEvacuationWidthObject(quote),
      minWidthMm,
      source: {
        document,
        clause: extractClause(quote),
        quote,
      },
      sourcePdf,
      applicability: extractApplicability(quote),
    });
  }

  return rules;
}

function dedupeRules(rules: EvacuationWidthNormRule[]): EvacuationWidthNormRule[] {
  const byQuote = new Map<string, EvacuationWidthNormRule>();
  for (const rule of rules) {
    byQuote.set(rule.source.quote, rule);
  }
  return [...byQuote.values()];
}

function scoreEvacuationWidthRule(
  rule: EvacuationWidthNormRule,
  preferObject: EvacuationWidthObject,
  buildingClass?: string
): number {
  let score = 0;
  if (rule.object === preferObject) score += 100;
  if (rule.object === "эвакуационный коридор") score += 80;
  if (rule.object === "коридор") score += 50;
  if (/ширин[а-яё]*\s+эвакуационн/i.test(rule.source.quote)) score += 40;
  if (rule.minWidthMm >= 1000 && rule.minWidthMm <= 1500) score += 25;
  if (buildingClass) {
    const haystack = `${rule.applicability ?? ""} ${rule.source.quote}`.toLowerCase();
    if (haystack.includes(buildingClass)) score += 60;
  }
  if (rule.source.clause) score += 10;
  return score;
}

export function pickPrimaryEvacuationWidthRule(
  rules: EvacuationWidthNormRule[],
  options?: { buildingClass?: string; preferObject?: EvacuationWidthObject }
): EvacuationWidthNormRule | null {
  const plausible = rules.filter((rule) =>
    isPlausibleCorridorWidthRule(rule.source.quote, rule.minWidthMm)
  );
  if (plausible.length === 0) return null;

  const preferObject = options?.preferObject ?? "эвакуационный коридор";
  const buildingClass = options?.buildingClass?.trim().toLowerCase();

  return [...plausible].sort(
    (a, b) =>
      scoreEvacuationWidthRule(b, preferObject, buildingClass) -
      scoreEvacuationWidthRule(a, preferObject, buildingClass)
  )[0];
}

export async function loadEvacuationWidthRulesFromNormatives(options?: {
  normativesDir?: string;
  pdfFiles?: string[];
  scanAllPdfs?: boolean;
}): Promise<{
  rules: EvacuationWidthNormRule[];
  warnings: string[];
  normativesDir: string;
}> {
  const normativesDir = options?.normativesDir ?? (await resolveNormativesDir());
  let pdfFiles = options?.pdfFiles ?? [...DEFAULT_EVACUATION_WIDTH_PDF_FILES];

  if (options?.scanAllPdfs) {
    pdfFiles = (await readdir(normativesDir)).filter((file) =>
      file.toLowerCase().endsWith(".pdf")
    );
  }

  const rules: EvacuationWidthNormRule[] = [];
  const warnings: string[] = [];

  for (const fileName of pdfFiles) {
    const pdfPath = join(normativesDir, fileName);
    try {
      const pdfBuffer = await readFile(pdfPath);
      const parsedPdf = await pdfParse(pdfBuffer);
      const document = normalizeDocumentName(fileName);
      const extracted = extractEvacuationWidthRulesFromText(
        parsedPdf.text,
        document,
        fileName
      );
      rules.push(...extracted);
      if (extracted.length === 0) {
        warnings.push(`В ${fileName} не найдено требований к ширине коридоров.`);
      }
    } catch (error) {
      warnings.push(
        `Не удалось прочитать ${fileName}: ${
          error instanceof Error ? error.message : String(error)
        }`
      );
    }
  }

  const deduped = dedupeRules(rules);
  if (deduped.length === 0) {
    warnings.push(
      `В каталоге normatives не извлечено ни одного правила по ширине коридоров (${normativesDir}).`
    );
  }

  return { rules: deduped, warnings, normativesDir };
}
