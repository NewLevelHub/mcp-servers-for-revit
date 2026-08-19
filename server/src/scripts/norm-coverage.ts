#!/usr/bin/env node
/**
 * How much of each norm PDF reached the rule library (REV-51).
 * Usage: npm run norms:coverage
 *
 * Prints a table and, with --misses, the requirement clauses that produced no
 * rule — the list that says where a check would answer «нарушений не найдено»
 * because it has nothing to check with.
 */
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import db from "../database/db.js";
import {
  analyzeDocumentCoverage,
  coverageBand,
  describeCoverage,
  type DocumentCoverage,
} from "../normatives/coverage.js";
import { resolveNormativesDir } from "../normatives/fireDoorRules.js";

const require = createRequire(import.meta.url);
const pdfParse = require("pdf-parse") as (
  buffer: Buffer,
  options?: { max?: number }
) => Promise<{ text: string; numpages: number }>;

const showMisses = process.argv.includes("--misses");

const normativesDir = await resolveNormativesDir();
const pdfs = fs
  .readdirSync(normativesDir)
  .filter((name) => name.toLowerCase().endsWith(".pdf"))
  .sort((a, b) => a.localeCompare(b, "ru"));

/**
 * Clause numbers held for a document. Matched by a normalised substring because
 * the library stores «СП РК 3.02-101-2012» while the file is
 * «SP_RK_3.02-101-2012_27.04.2021.pdf» — the digits are what survive both.
 */
const stored = db
  .prepare("SELECT document, clause FROM norm_rules")
  .all() as Array<{ document: string; clause: string }>;

const digitsOf = (text: string) => (text.match(/\d[\d.-]*/g) ?? []).join("");

function storedFor(fileName: string): { clauses: string[]; count: number } {
  const fileDigits = digitsOf(fileName);
  const clauses: string[] = [];
  let count = 0;
  for (const row of stored) {
    const docDigits = digitsOf(row.document);
    if (!docDigits || !fileDigits.includes(docDigits)) continue;
    count += 1;
    if (row.clause) clauses.push(row.clause);
  }
  return { clauses, count };
}

const results: Array<DocumentCoverage & { pages: number; file: string }> = [];

for (const fileName of pdfs) {
  const buffer = fs.readFileSync(path.join(normativesDir, fileName));
  const parsed = await pdfParse(buffer);
  const { clauses, count } = storedFor(fileName);

  results.push({
    ...analyzeDocumentCoverage({
      document: fileName.replace(/\.pdf$/i, ""),
      text: parsed.text,
      storedClauses: clauses,
      rulesInLibrary: count,
      maxSamples: showMisses ? 8 : 3,
      pages: parsed.numpages,
    }),
    pages: parsed.numpages,
    file: fileName,
  });
}

results.sort((a, b) => a.coveragePercent - b.coveragePercent);

const MARK: Record<string, string> = { good: "OK  ", partial: "ЧАСТЬ", thin: "ТОНКО" };
/** Segmentation failure outranks the percentage — the percentage means nothing there. */
const markFor = (r: DocumentCoverage) =>
  r.structureSuspect ? "НЕ РАЗОБРАН" : MARK[coverageBand(r.coveragePercent)];

console.log(
  "документ".padEnd(40),
  "стр".padStart(4),
  "треб".padStart(5),
  "разоб".padStart(6),
  "%".padStart(4),
  "правил".padStart(7),
  "  оценка"
);
console.log("-".repeat(88));

for (const r of results) {
  console.log(
    r.document.slice(0, 40).padEnd(40),
    String(r.pages).padStart(4),
    String(r.requirementClauses).padStart(5),
    String(r.coveredClauses).padStart(6),
    String(r.coveragePercent).padStart(4),
    String(r.rulesInLibrary).padStart(7),
    "  " + markFor(r)
  );
}

const totals = results.reduce(
  (acc, r) => ({
    requirement: acc.requirement + r.requirementClauses,
    covered: acc.covered + r.coveredClauses,
    rules: acc.rules + r.rulesInLibrary,
  }),
  { requirement: 0, covered: 0, rules: 0 }
);

console.log("-".repeat(88));
console.log(
  "ИТОГО".padEnd(40),
  "".padStart(4),
  String(totals.requirement).padStart(5),
  String(totals.covered).padStart(6),
  String(
    totals.requirement === 0
      ? 100
      : Math.round((totals.covered / totals.requirement) * 100)
  ).padStart(4),
  String(totals.rules).padStart(7)
);

const thin = results.filter(
  (r) => r.structureSuspect || coverageBand(r.coveragePercent) === "thin"
);
if (thin.length > 0) {
  console.log("\nПочти не разобраны — проверки по ним ничего не доказывают:");
  for (const r of thin) console.log("  • " + describeCoverage(r));
}

if (showMisses) {
  console.log("\n\nПропущенные требования (по документам с худшим покрытием):");
  for (const r of results.slice(0, 5)) {
    if (r.missedSamples.length === 0) continue;
    console.log(`\n--- ${r.document} (${r.coveragePercent}%) ---`);
    for (const miss of r.missedSamples) {
      console.log(`  п. ${miss.clause}${miss.hasNumber ? " [есть число]" : ""}`);
      console.log(`     ${miss.excerpt}`);
    }
  }
} else {
  console.log("\nСписок пропущенных требований: npm run norms:coverage -- --misses");
}
