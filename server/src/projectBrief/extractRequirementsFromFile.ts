import { readFile } from "node:fs/promises";
import { extname } from "node:path";
import pdfParse from "pdf-parse";
import mammoth from "mammoth";
import {
  type BriefExtractionInput,
  type BriefExtractionResult,
} from "./types.js";
import { extractRequirementsFromText } from "./extractRequirements.js";

export interface BriefFileExtractionInput
  extends Omit<BriefExtractionInput, "text"> {
  filePath: string;
  maxPages?: number;
}

/** Matches normatives/extractRulesFromPdf.ts — same "is this actually a scan" bar. */
export const MIN_EMBEDDED_TEXT_CHARS = 80;
export const MIN_CHARS_PER_PAGE_FOR_TEXT_PDF = 120;

async function readPdfText(filePath: string, maxPages?: number): Promise<{ text: string; warnings: string[] }> {
  const buffer = await readFile(filePath);
  const parsed = await pdfParse(buffer, { max: maxPages });
  const text = (parsed.text ?? "").trim();
  const pages = Math.max(1, parsed.numpages || 1);
  const charsPerPage = text.length / pages;
  const likelyScanned = text.length < MIN_EMBEDDED_TEXT_CHARS || charsPerPage < MIN_CHARS_PER_PAGE_FOR_TEXT_PDF;

  const warnings: string[] = [];
  if (text.length === 0) {
    warnings.push(
      "PDF has no extractable text (likely a scan/image PDF). OCR is out of scope — " +
        "preprocess with OCR and re-run, or supply a text PDF/DOCX."
    );
  } else if (likelyScanned) {
    warnings.push(
      `PDF text looks too sparse for reliable extraction (${text.length} chars, ` +
        `~${Math.round(charsPerPage)} chars/page across ${pages} page(s)). Likely a scan; ` +
        "requirements from this file may be incomplete."
    );
  }

  return { text, warnings };
}

async function readDocxText(filePath: string): Promise<{ text: string; warnings: string[] }> {
  const buffer = await readFile(filePath);
  const result = await mammoth.extractRawText({ buffer });
  const text = (result.value ?? "").trim();
  const warnings = result.messages
    .filter((m) => m.type === "error")
    .map((m) => `mammoth: ${m.message}`);

  if (text.length === 0) {
    warnings.push("DOCX produced no extractable text — file may be empty or corrupt.");
  }

  return { text, warnings };
}

/**
 * Reads a PDF or DOCX project brief and extracts structured requirements from
 * its text. File type is chosen by extension (.pdf / .docx); anything else is
 * refused rather than guessed at.
 */
export async function extractRequirementsFromFile(
  input: BriefFileExtractionInput
): Promise<BriefExtractionResult> {
  const { filePath, maxPages, ...rest } = input;
  const ext = extname(filePath).toLowerCase();

  let text: string;
  let readWarnings: string[];

  if (ext === ".pdf") {
    ({ text, warnings: readWarnings } = await readPdfText(filePath, maxPages));
  } else if (ext === ".docx") {
    ({ text, warnings: readWarnings } = await readDocxText(filePath));
  } else {
    throw new Error(`Unsupported file type "${ext}" — expected .pdf or .docx.`);
  }

  if (text.length === 0) {
    return { requirements: [], warnings: [...readWarnings, `Source: empty-text file (${filePath}).`] };
  }

  const result = extractRequirementsFromText({ ...rest, text });
  return {
    requirements: result.requirements,
    warnings: [...readWarnings, ...result.warnings, `Source: ${filePath}.`],
  };
}
