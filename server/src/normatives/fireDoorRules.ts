import { createHash } from "node:crypto";
import { access } from "node:fs/promises";
import { readFile, readdir, stat } from "node:fs/promises";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import pdfParse from "pdf-parse";
import type { NormativeSourceRef } from "./types.js";

/** Pilot PDF set for residential blocks («Короткий блок» and similar). */
export const DEFAULT_FIRE_DOOR_PDF_FILES = [
  "SP_RK_3.02-101-2012_27.04.2021.pdf",
  "SP_RK_3.02-109-2012_07.08.2018.pdf",
  "СН РК_3.02-09-2019.pdf",
  "СН РК_3.02-01-2023.pdf",
  "Тех.регламент Общие требования к пожарной.pdf",
] as const;

export type FireDoorScenario =
  | "egress-route"
  | "between-compartments"
  | "stair-to-corridor"
  | "evacuation-exit"
  | "fire-compartment-door";

export interface FireDoorNormRule {
  id: string;
  scenario: FireDoorScenario;
  reason: string;
  source: NormativeSourceRef;
  sourcePdf: string;
}

const CLAUSE_PREFIX_RE =
  /(?:^|\s)(?:п\.?\s*|§\s*)(\d+(?:\.\d+)*)\s*[.:)\-–—]?\s*/i;

/**
 * Real fire-door requirement language only.
 * Do NOT match «двер…эвакуац» alone — that catches path-length rules
 * (e.g. «от двери… путь эвакуации… не более 30 м») which are not ПД requirements.
 */
const FIRE_DOOR_REQUIREMENT_RE =
  /противопожарн|самозакрывающ|предел\w*\s+огнестойк|огнестойк\w*\s+двер|\bEI\s*\d{2,3}\b/i;

const DOCUMENT_NAME_OVERRIDES: Record<string, string> = {
  "SP_RK_3.02-101-2012_27.04.2021.pdf": "СП РК 3.02-101-2012",
  "SP_RK_3.02-109-2012_07.08.2018.pdf": "СП РК 3.02-109-2012",
  "СН РК_3.02-09-2019.pdf": "СН РК 3.02-09-2019",
  "СН РК_3.02-01-2023.pdf": "СН РК 3.02-01-2023",
  "Тех.регламент Общие требования к пожарной.pdf":
    'ТР "Общие требования к пожарной безопасности"',
};

async function pathExists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

function repoRootFromModule(): string {
  const moduleDir = dirname(fileURLToPath(import.meta.url));
  // src/normatives or build/normatives → repo root is two levels up from server/
  return resolve(moduleDir, "..", "..", "..");
}

/** Resolve <repo>/normatives regardless of MCP server process cwd. */
export async function resolveNormativesDir(): Promise<string> {
  const candidates = [
    process.env.REVIT_MCP_NORMATIVES_DIR,
    resolve(repoRootFromModule(), "normatives"),
    resolve(process.cwd(), "normatives"),
    resolve(process.cwd(), "..", "normatives"),
    resolve(process.cwd(), "mcp-servers-for-revit", "normatives"),
  ].filter((value): value is string => Boolean(value));

  for (const candidate of candidates) {
    if (await pathExists(candidate)) {
      return candidate;
    }
  }

  return candidates[0] ?? resolve(repoRootFromModule(), "normatives");
}

export function normalizeDocumentName(fileName: string): string {
  if (DOCUMENT_NAME_OVERRIDES[fileName]) {
    return DOCUMENT_NAME_OVERRIDES[fileName];
  }

  const stem = basename(fileName, ".pdf");
  if (/^SP_RK/i.test(stem)) {
    const code = stem.match(/3\.\d{2}-\d{3}/)?.[0];
    return code ? `СП РК ${code}` : stem.replace(/_/g, " ");
  }
  if (/^СН\s*РК/i.test(stem) || /^SN/i.test(stem)) {
    const code = stem.match(/3\.\d{2}-\d{2,3}/)?.[0];
    return code ? `СН РК ${code}` : stem;
  }
  if (/^СП\s*РК/i.test(stem)) {
    const code = stem.match(/3\.\d{2}-\d{3}/)?.[0];
    return code ? `СП РК ${code}` : stem;
  }
  return stem;
}

function extractClause(sentence: string): string {
  const match = sentence.match(CLAUSE_PREFIX_RE);
  return match ? `п. ${match[1]}` : "";
}

/** True when quote is an actual fire-door / self-closing / EI requirement. */
export function isFireDoorRequirementQuote(quote: string): boolean {
  return FIRE_DOOR_REQUIREMENT_RE.test(quote);
}

export function inferFireDoorScenario(sentence: string): FireDoorScenario {
  const normalized = sentence.toLowerCase();

  if (/лестничн/.test(normalized) && /(коридор|квартир|холл)/.test(normalized)) {
    return "stair-to-corridor";
  }
  if (
    /пожарн\w*\s+отсек|противопожарн\w*\s+преград|противопожарн\w*\s+перегород/.test(
      normalized
    )
  ) {
    return "between-compartments";
  }
  if (/эвакуационн\w*\s+выход|выход\w*[^.]{0,40}эвакуац/.test(normalized)) {
    return "evacuation-exit";
  }
  if (
    /противопожарн\w*.{0,60}(путь\w*\s+эвакуац|эвакуационн\w*\s+путь|коридор)|(путь\w*\s+эвакуац|эвакуационн\w*\s+путь|коридор).{0,60}противопожарн/.test(
      normalized
    )
  ) {
    return "egress-route";
  }
  return "fire-compartment-door";
}

export function inferFireDoorReason(scenario: FireDoorScenario): string {
  switch (scenario) {
    case "egress-route":
      return "Дверь на пути эвакуации";
    case "between-compartments":
      return "Дверь между пожарными отсеками / ограждением преграды";
    case "stair-to-corridor":
      return "Дверь между лестничной клеткой и коридором/квартирой";
    case "evacuation-exit":
      return "Дверь эвакуационного выхода";
    default:
      return "Дверь в ограждении противопожарной преграды";
  }
}

export function extractFireDoorRulesFromText(
  text: string,
  document: string,
  sourcePdf: string
): FireDoorNormRule[] {
  const sentences = text.replace(/\r/g, "").split(/(?<=[.!?])\s+/);
  const rules: FireDoorNormRule[] = [];
  const seen = new Set<string>();

  for (const sentence of sentences) {
    if (!/двер/i.test(sentence)) continue;
    if (!isFireDoorRequirementQuote(sentence)) continue;

    const quote = sentence.replace(/\s+/g, " ").trim();
    if (quote.length < 35 || seen.has(quote)) continue;
    seen.add(quote);

    const scenario = inferFireDoorScenario(quote);
    rules.push({
      id: createHash("sha1").update(quote).digest("hex").slice(0, 12),
      scenario,
      reason: inferFireDoorReason(scenario),
      source: {
        document,
        clause: extractClause(quote),
        quote,
      },
      sourcePdf,
    });
  }

  return rules;
}

function dedupeRules(rules: FireDoorNormRule[]): FireDoorNormRule[] {
  const byQuote = new Map<string, FireDoorNormRule>();
  for (const rule of rules) {
    byQuote.set(rule.source.quote, rule);
  }
  return [...byQuote.values()];
}

export interface FireDoorRulesResult {
  rules: FireDoorNormRule[];
  warnings: string[];
  normativesDir: string;
}

/**
 * Parsed rules keyed by the files they came from (REV-53).
 *
 * Every call used to re-read and re-parse the same PDFs. Measured on «Короткий
 * блок»: the Revit side of check_fire_doors takes ~4 s for 483 doors, while the
 * tool as a whole took 37 s — the other 33 s were spent parsing five documents
 * that had not changed since the previous call minutes earlier.
 *
 * Removing the 60-page cap in REV-51 made this worse, not better: the same five
 * documents are now parsed in full, including the 145-page fire-safety
 * regulation. Correct rules, paid for on every single check.
 */
const fireDoorRulesCache = new Map<string, FireDoorRulesResult>();

/** Files plus mtime and size — edit a PDF and the key stops matching. */
async function cacheKeyFor(dir: string, files: string[]): Promise<string> {
  const parts = await Promise.all(
    [...files].sort().map(async (file) => {
      try {
        const info = await stat(join(dir, file));
        return `${file}:${info.mtimeMs}:${info.size}`;
      } catch {
        return `${file}:missing`;
      }
    })
  );
  return `${dir}|${parts.join("|")}`;
}

/** Drop the cache — for tests, and after a reseed replaces the PDFs. */
export function clearFireDoorRulesCache(): void {
  fireDoorRulesCache.clear();
}

export async function loadFireDoorRulesFromNormatives(options?: {
  normativesDir?: string;
  pdfFiles?: string[];
  scanAllPdfs?: boolean;
}): Promise<FireDoorRulesResult> {
  const normativesDir = options?.normativesDir ?? (await resolveNormativesDir());
  let pdfFiles = options?.pdfFiles ?? [...DEFAULT_FIRE_DOOR_PDF_FILES];

  if (options?.scanAllPdfs) {
    pdfFiles = (await readdir(normativesDir)).filter((file) =>
      file.toLowerCase().endsWith(".pdf")
    );
  }

  const cacheKey = await cacheKeyFor(normativesDir, pdfFiles);
  const cached = fireDoorRulesCache.get(cacheKey);
  if (cached) return cached;

  const rules: FireDoorNormRule[] = [];
  const warnings: string[] = [];

  for (const fileName of pdfFiles) {
    const pdfPath = join(normativesDir, fileName);
    try {
      const pdfBuffer = await readFile(pdfPath);
      const parsedPdf = await pdfParse(pdfBuffer);
      const document = normalizeDocumentName(fileName);
      const extracted = extractFireDoorRulesFromText(
        parsedPdf.text,
        document,
        fileName
      );
      rules.push(...extracted);
      if (extracted.length === 0) {
        warnings.push(`В ${fileName} не найдено требований к противопожарным дверям.`);
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
      `В каталоге normatives не извлечено ни одного правила по противопожарным дверям (${normativesDir}).`
    );
  }

  const result: FireDoorRulesResult = { rules: deduped, warnings, normativesDir };
  fireDoorRulesCache.set(cacheKey, result);
  return result;
}
