import { readdir } from "node:fs/promises";
import { basename, join } from "node:path";
import type DatabaseConstructor from "better-sqlite3";
import { extractRulesFromPdfFile } from "./extractRulesFromPdf.js";
import {
  normalizeDocumentName,
  resolveNormativesDir,
} from "./fireDoorRules.js";
import {
  getNormLibraryStats,
  saveNormRules,
  withSuggestedTags,
  type NormLibraryStats,
} from "./rulesStore.js";
import { ensureCuratedResidentialRoomNorms } from "./normAudit/curatedResidentialRoomNorms.js";
import { ensureCuratedGost21101Rules } from "./curatedGost21101Rules.js";
import { exportNormCatalog } from "./exportNormCatalog.js";

type Database = DatabaseConstructor.Database;

export interface SeedNormLibraryOptions {
  /** Override norms folder (defaults to resolveNormativesDir). */
  normativesDir?: string;
  /** Max PDF pages to parse per file (keeps seed bounded). */
  maxPages?: number;
  /** Prefer numeric / checkable rules when seeding (drops pure notes). */
  preferNumericRules?: boolean;
}

export interface SeedNormFileResult {
  fileName: string;
  document: string;
  ruleCount: number;
  inserted: number;
  updated: number;
  skipped: boolean;
  /** True when PDF text was empty/sparse (scan) — see warnings. */
  likelyScanned?: boolean;
  warnings?: string[];
  error?: string;
}

export interface SeedNormLibraryResult {
  normativesDir: string;
  filesProcessed: number;
  filesFailed: number;
  inserted: number;
  updated: number;
  files: SeedNormFileResult[];
  library: NormLibraryStats;
}

function documentVersionFromFileName(fileName: string): string | undefined {
  const stem = basename(fileName, ".pdf");
  const dated = stem.match(/(\d{2}\.\d{2}\.\d{4})/);
  if (dated) return dated[1];
  if (/-97/i.test(stem) || /_97/i.test(stem)) return "97";
  return undefined;
}

/**
 * Extract rules from every PDF in normatives/ and upsert into SQLite.
 * Idempotent via rule_key (document+clause+object+type).
 */
export async function seedNormLibrary(
  db: Database,
  options: SeedNormLibraryOptions = {}
): Promise<SeedNormLibraryResult> {
  const normativesDir =
    options.normativesDir ?? (await resolveNormativesDir());
  // Undefined = read the whole document. The old default of 60 pages silently
  // truncated every long norm: СП РК 3.02-107 (197 pages) yielded 104 rules
  // instead of 518, СП РК 3.01-101 (279 pages) 31 instead of 404. Across the 19
  // PDFs the cap cost 2216 of 3651 extractable rules — more than the library
  // held — and nothing said so: seeding reported success, and a check with no
  // rule behind it answers "нарушений не найдено" (REV-51).
  const maxPages = options.maxPages;
  const preferNumeric = options.preferNumericRules !== false;

  const entries = await readdir(normativesDir);
  const pdfs = entries
    .filter((name) => name.toLowerCase().endsWith(".pdf"))
    .sort((a, b) => a.localeCompare(b, "ru"));

  const files: SeedNormFileResult[] = [];
  let inserted = 0;
  let updated = 0;
  let filesFailed = 0;

  for (const fileName of pdfs) {
    const pdfPath = join(normativesDir, fileName);
    const document = normalizeDocumentName(fileName);
    try {
      const extracted = await extractRulesFromPdfFile({
        pdfPath,
        document,
        maxPages,
      });

      const scanWarnings = extracted.warnings.filter(
        (w) =>
          /scan|OCR|sparse|no extractable text/i.test(w)
      );
      const likelyScanned = scanWarnings.length > 0;

      let rules = withSuggestedTags(extracted.rules);
      if (preferNumeric) {
        const numeric = rules.filter((rule) =>
          ["min_value", "max_value", "range", "exact_value"].includes(rule.type)
        );
        // Keep qualitative rules if extract produced nothing numeric.
        if (numeric.length > 0) rules = numeric;
      }

      if (rules.length === 0) {
        files.push({
          fileName,
          document,
          ruleCount: 0,
          inserted: 0,
          updated: 0,
          skipped: true,
          likelyScanned,
          warnings: scanWarnings.length > 0 ? scanWarnings : undefined,
          error: likelyScanned
            ? "No rules extracted — PDF looks like a scan / missing text layer."
            : undefined,
        });
        continue;
      }

      const saveResult = saveNormRules(db, rules, {
        documentVersion: documentVersionFromFileName(fileName),
      });
      inserted += saveResult.inserted;
      updated += saveResult.updated;
      files.push({
        fileName,
        document,
        ruleCount: rules.length,
        inserted: saveResult.inserted,
        updated: saveResult.updated,
        skipped: false,
        likelyScanned,
        warnings: scanWarnings.length > 0 ? scanWarnings : undefined,
      });
    } catch (error) {
      filesFailed += 1;
      files.push({
        fileName,
        document,
        ruleCount: 0,
        inserted: 0,
        updated: 0,
        skipped: true,
        error: error instanceof Error ? error.message : String(error),
      });
    }
  }

  const curatedResidential = ensureCuratedResidentialRoomNorms(db);
  inserted += curatedResidential.inserted;
  updated += curatedResidential.updated;

  const curatedGost = ensureCuratedGost21101Rules(db);
  inserted += curatedGost.inserted;
  updated += curatedGost.updated;

  // Keep in-Revit assistant catalog in sync after every seed.
  try {
    await exportNormCatalog(db);
  } catch (error) {
    // Seed itself succeeded; export is best-effort for the plugin.
    const detail = error instanceof Error ? error.message : String(error);
    files.push({
      fileName: "(export norm-catalog.json)",
      document: "plugin catalog",
      ruleCount: 0,
      inserted: 0,
      updated: 0,
      skipped: true,
      warnings: [`exportNormCatalog failed: ${detail}`],
    });
  }

  return {
    normativesDir,
    filesProcessed: pdfs.length,
    filesFailed,
    inserted,
    updated,
    files,
    library: getNormLibraryStats(db),
  };
}
