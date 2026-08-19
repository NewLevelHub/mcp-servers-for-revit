/**
 * How much of a norm document actually reached the rule library (REV-51).
 *
 * Extraction is pattern-based: a clause becomes a rule only when a number with a
 * recognised unit, or a requirement word («должен», «не допускается»), is found
 * in it. Everything phrased differently — tables, appendices, prose — is dropped
 * silently. Nothing reported that, so the library looked complete at 1715 rules
 * while the fire-safety technical regulation had contributed 6 rules out of 145
 * pages, and ГОСТ 21.101-97 had contributed 2 out of 36.
 *
 * The number that matters is not "rules per page" — a page of definitions
 * legitimately yields none. It is: **of the clauses that read like requirements,
 * how many produced a rule?** A clause saying «должна быть не менее 1,5 м» that
 * produced nothing is a hole; a clause defining a term is not.
 *
 * This measures the extractor against the document, so it can only be as honest
 * as {@link REQUIREMENT_MARKERS}. A requirement worded without any of these is
 * invisible here too — which is why {@link DocumentCoverage.missedSamples} exists:
 * read the misses, do not just trust the percentage.
 */
import { splitIntoSegments } from "./extractRules.js";

/**
 * Wording that makes a clause a requirement rather than a definition.
 * Russian and Kazakh, matching the extractor's own vocabulary.
 */
export const REQUIREMENT_MARKERS: readonly RegExp[] = [
  /не\s+менее/i,
  /не\s+более/i,
  /не\s+допускается/i,
  /запрещ/i,
  /должн/i,
  /следует\s+принимать/i,
  /необходимо/i,
  /предусматрив/i,
  /болуы\s+тиіс/i,
  /рұқсат\s+етілмейді/i,
  /тыйым\s+салынады/i,
  /көзделеді/i,
];

/** A clause that states a requirement — the population coverage is measured over. */
export function looksLikeRequirement(text: string): boolean {
  return REQUIREMENT_MARKERS.some((marker) => marker.test(text));
}

export interface MissedClause {
  clause: string;
  /** Trimmed for reading; the full text stays in the PDF. */
  excerpt: string;
  /** True when the clause carries a number — the most likely real loss. */
  hasNumber: boolean;
}

export interface DocumentCoverage {
  document: string;
  /** Clauses the extractor's own segmentation found. */
  totalClauses: number;
  /** Clauses worded as requirements. */
  requirementClauses: number;
  /** Requirement clauses represented in the library. */
  coveredClauses: number;
  /** coveredClauses / requirementClauses, 0–100, rounded. 100 when nothing to cover. */
  coveragePercent: number;
  /** Rules stored for this document. */
  rulesInLibrary: number;
  /** Requirement clauses with no rule — worst first (those carrying numbers). */
  missedSamples: MissedClause[];
  /**
   * The document barely split into clauses at all — so few requirements were
   * found per page that the percentage above describes the parser, not the
   * library. Reported separately because the two need opposite fixes: a low
   * percentage means teaching the extractor more patterns, this means the text
   * layer or the clause numbering never came through.
   */
  structureSuspect: boolean;
}

/**
 * Below this many requirement clauses per page, assume segmentation failed.
 * A real norm states far more than one requirement per two pages; the fire-safety
 * regulation reporting 5 across 145 pages is a parse failure, not a terse document.
 */
export const MIN_REQUIREMENTS_PER_PAGE = 0.5;

const EXCERPT_CHARS = 160;

function excerpt(text: string): string {
  const clean = text.replace(/\s+/g, " ").trim();
  return clean.length <= EXCERPT_CHARS ? clean : `${clean.slice(0, EXCERPT_CHARS)}…`;
}

/** A clause number as stored in the library: «п. 4.3.2.12» → «4.3.2.12». */
export function normalizeClause(clause: string): string {
  return clause
    // Longest alternative first: `п\.?` would otherwise eat the «п» of «пункт»
    // and leave «ункт 5.1», which then matches no stored clause.
    .replace(/^\s*(?:пункт|clause|п\.?)\s*/i, "")
    .replace(/[.\s]+$/, "")
    .trim();
}

export interface CoverageInput {
  document: string;
  /** Full text of the document, as pdf-parse returns it. */
  text: string;
  /** Clause numbers already in the library for this document. */
  storedClauses: readonly string[];
  /** Total rules stored for this document. */
  rulesInLibrary: number;
  /** How many misses to list. */
  maxSamples?: number;
  /** Page count, when known — enables the segmentation-failure check. */
  pages?: number;
}

/**
 * Compare one document against what the library holds for it.
 *
 * A clause counts as covered when the library has any rule bearing its number.
 * That is deliberately generous — one rule out of a clause stating three limits
 * still counts — so the real loss is at least what this reports, never less.
 */
export function analyzeDocumentCoverage(input: CoverageInput): DocumentCoverage {
  const stored = new Set(input.storedClauses.map(normalizeClause).filter(Boolean));
  const segments = splitIntoSegments(input.text);

  let requirementClauses = 0;
  let coveredClauses = 0;
  const missed: MissedClause[] = [];

  for (const segment of segments) {
    if (!looksLikeRequirement(segment.text)) continue;
    requirementClauses += 1;

    const clause = normalizeClause(segment.clause);
    if (clause && stored.has(clause)) {
      coveredClauses += 1;
      continue;
    }

    missed.push({
      clause: clause || "—",
      excerpt: excerpt(segment.text),
      hasNumber: /\d/.test(segment.text),
    });
  }

  // A clause with a number in it is the likelier real loss: the extractor was
  // built for exactly those, so missing one means a pattern did not fire.
  missed.sort((a, b) => Number(b.hasNumber) - Number(a.hasNumber));

  return {
    document: input.document,
    totalClauses: segments.length,
    requirementClauses,
    coveredClauses,
    coveragePercent:
      requirementClauses === 0
        ? 100
        : Math.round((coveredClauses / requirementClauses) * 100),
    rulesInLibrary: input.rulesInLibrary,
    missedSamples: missed.slice(0, input.maxSamples ?? 10),
    structureSuspect:
      input.pages != null &&
      input.pages > 0 &&
      requirementClauses / input.pages < MIN_REQUIREMENTS_PER_PAGE,
  };
}

/** Bands for reporting. Chosen to be read at a glance, not to be precise. */
export type CoverageBand = "good" | "partial" | "thin";

export function coverageBand(percent: number): CoverageBand {
  if (percent >= 70) return "good";
  if (percent >= 30) return "partial";
  return "thin";
}

/**
 * One line an architect can act on. Said plainly, because the consequence is
 * plain: below a certain point, "нарушений не найдено" means "я не читал".
 */
export function describeCoverage(coverage: DocumentCoverage): string {
  if (coverage.structureSuspect) {
    return (
      `${coverage.document}: документ не разобрался на пункты — найдено всего ` +
      `${coverage.requirementClauses} требований на ${coverage.rulesInLibrary} правил в библиотеке. ` +
      `Процент здесь описывает разборщик, а не библиотеку: проверить текстовый слой PDF и нумерацию пунктов.`
    );
  }
  const band = coverageBand(coverage.coveragePercent);
  const head =
    `${coverage.document}: разобрано ${coverage.coveragePercent}% требований ` +
    `(${coverage.coveredClauses} из ${coverage.requirementClauses}), ` +
    `правил в библиотеке ${coverage.rulesInLibrary}`;

  if (band === "thin") {
    return `${head}. Документ почти не разобран — проверки по нему опираться не на что, ` +
      `«нарушений не найдено» здесь ничего не значит.`;
  }
  if (band === "partial") {
    return `${head}. Разобрана часть — отсутствие нарушений не доказывает соответствие.`;
  }
  return `${head}.`;
}
