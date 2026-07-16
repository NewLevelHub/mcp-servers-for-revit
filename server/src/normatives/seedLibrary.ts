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
  const maxPages = options.maxPages ?? 60;
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

  const curated = ensureCuratedResidentialRoomNorms(db);
  inserted += curated.inserted;
  updated += curated.updated;

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
