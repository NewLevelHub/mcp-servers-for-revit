import { readFile } from "node:fs/promises";
import pdfParse from "pdf-parse";
import {
  type NormativeExtractionInput,
  type NormativeExtractionResult,
  normativeExtractionInputSchema,
} from "./types.js";
import { extractRulesFromText } from "./extractRules.js";

export interface NormativePdfExtractionInput
  extends Omit<NormativeExtractionInput, "text"> {
  pdfPath: string;
  maxPages?: number;
}

/** Heuristic: pure image scans / empty OCR yield almost no characters. */
export const MIN_EMBEDDED_TEXT_CHARS = 80;
/** Below this average chars/page, treat as likely scan (even if not empty). */
export const MIN_CHARS_PER_PAGE_FOR_TEXT_PDF = 120;

export interface PdfTextQuality {
  text: string;
  pageCount: number;
  charCount: number;
  charsPerPage: number;
  /** True when embedded text is missing or too sparse for reliable extract. */
  likelyScanned: boolean;
  warnings: string[];
}

export function assessPdfTextQuality(
  text: string,
  pageCount: number
): PdfTextQuality {
  const trimmed = text.trim();
  const charCount = trimmed.length;
  const pages = Math.max(1, pageCount || 1);
  const charsPerPage = charCount / pages;
  const likelyScanned =
    charCount < MIN_EMBEDDED_TEXT_CHARS ||
    charsPerPage < MIN_CHARS_PER_PAGE_FOR_TEXT_PDF;

  const warnings: string[] = [];
  if (charCount === 0) {
    warnings.push(
      "PDF has no extractable text (likely a scan/image PDF). " +
        "OCR is out of scope for this pipeline — preprocess with OCR and re-run extract, " +
        "or use curated rules / a text PDF."
    );
  } else if (likelyScanned) {
    warnings.push(
      `PDF text looks too sparse for reliable extraction (${charCount} chars, ` +
        `~${Math.round(charsPerPage)} chars/page across ${pages} page(s)). ` +
        "Likely a scan with weak/missing OCR. Preprocess with OCR or supply a text PDF; " +
        "rules from this file may be incomplete."
    );
  }

  return {
    text: trimmed,
    pageCount: pages,
    charCount,
    charsPerPage,
    likelyScanned,
    warnings,
  };
}

function detectLanguage(text: string): "ru" | "kz" | "mixed" | "unknown" {
  const sample = text.slice(0, 5000).toLowerCase();
  const ruHits = (sample.match(/[ыэъё]/g) ?? []).length;
  const kzHits = (sample.match(/[әғқңөұүһі]/g) ?? []).length;

  if (ruHits === 0 && kzHits === 0) return "unknown";
  if (ruHits > 0 && kzHits > 0) return "mixed";
  return kzHits > 0 ? "kz" : "ru";
}

/**
 * Read a text-based PDF and extract normative rules from its text content.
 * Pure image scans are detected and return an explicit warning (OCR stays
 * out of scope — preprocess externally, then re-run extract).
 */
export async function extractRulesFromPdfFile(
  input: NormativePdfExtractionInput
): Promise<NormativeExtractionResult> {
  const { pdfPath, maxPages, ...rest } = input;

  const pdfBuffer = await readFile(pdfPath);
  const parsedPdf = await pdfParse(pdfBuffer, {
    max: maxPages,
  });

  const quality = assessPdfTextQuality(
    parsedPdf.text ?? "",
    parsedPdf.numpages ?? 0
  );

  if (quality.charCount === 0) {
    return {
      rules: [],
      warnings: [
        ...quality.warnings,
        `Source: empty-text PDF (${pdfPath}).`,
      ],
      metadata: {
        mode: rest.metadata?.mode ?? "embedded-text",
        confidence: rest.metadata?.confidence,
        extractedAt: rest.metadata?.extractedAt ?? new Date().toISOString(),
      },
    };
  }

  const parsedInput = normativeExtractionInputSchema.parse({
    ...rest,
    text: quality.text,
  });

  const result = extractRulesFromText(parsedInput);
  const language = detectLanguage(quality.text);

  return {
    ...result,
    warnings: [
      ...quality.warnings,
      ...result.warnings,
      `Detected language: ${language}. Source: text PDF (${pdfPath}).`,
    ],
  };
}
