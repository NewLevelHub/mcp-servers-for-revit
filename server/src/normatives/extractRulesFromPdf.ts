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
 * OCR is intentionally out of scope here: scanned PDFs should be preprocessed
 * by a dedicated OCR module and then passed to extractRulesFromText().
 */
export async function extractRulesFromPdfFile(
  input: NormativePdfExtractionInput
): Promise<NormativeExtractionResult> {
  const { pdfPath, maxPages, ...rest } = input;

  const pdfBuffer = await readFile(pdfPath);
  const parsedPdf = await pdfParse(pdfBuffer, {
    max: maxPages,
  });

  const parsedInput = normativeExtractionInputSchema.parse({
    ...rest,
    text: parsedPdf.text,
  });

  const result = extractRulesFromText(parsedInput);
  const language = detectLanguage(parsedPdf.text);

  return {
    ...result,
    warnings: [
      ...result.warnings,
      `Detected language: ${language}. Source: text PDF (${pdfPath}).`,
    ],
  };
}

