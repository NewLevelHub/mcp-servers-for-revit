#!/usr/bin/env node
/**
 * Does the committed norm catalog still match what the PDFs produce? (REV-52)
 * Usage: npm run norms:verify
 *
 * `plugin/Resources/norm-catalog.json` is the one norm artifact under version
 * control, and the in-Revit assistant reads it directly — so it is what makes
 * every machine, and every architect, work from the same norms. The SQLite
 * library is rebuilt locally from the same committed PDFs and never travels.
 *
 * That only holds while the committed catalog matches the PDFs. Edit a PDF,
 * change an extractor pattern, forget to re-export, and the assistant in Revit
 * quietly applies yesterday's norms while the MCP checks apply today's. Nothing
 * would say so — which is the whole failure mode this file exists to prevent.
 *
 * Seeds a throwaway database from the repo PDFs, exports from it, and compares
 * rule for rule. Exit 1 on drift, with the fix printed.
 */
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { createRequire } from "node:module";
import {
  DEFAULT_NORM_CATALOG_PATH,
  exportNormCatalog,
} from "../normatives/exportNormCatalog.js";
import { seedNormLibrary } from "../normatives/seedLibrary.js";

const require = createRequire(import.meta.url);
const Database = require("better-sqlite3");

const committedPath = DEFAULT_NORM_CATALOG_PATH;
if (!fs.existsSync(committedPath)) {
  console.error(`Нет ${committedPath}. Собрать: npm run seed:norms`);
  process.exit(1);
}

const tmpDb = path.join(
  fs.mkdtempSync(path.join(os.tmpdir(), "norm-verify-")),
  "verify.db"
);

let freshRules: Array<{ id: string }> = [];
try {
  const db = new Database(tmpDb);
  // prune keeps the throwaway library a pure function of the PDFs, so a stale
  // row here cannot mask real drift.
  const seeded = await seedNormLibrary(db, { prune: true, catalogPath: null });
  // Write to the throwaway folder: verification must never touch the file it checks.
  const fresh = await exportNormCatalog(db, path.join(path.dirname(tmpDb), "fresh.json"));
  freshRules = fresh.rules as Array<{ id: string }>;
  db.close();
  console.log(
    `Пересев из ${seeded.filesProcessed} PDF: ${freshRules.length} правил ` +
      `(ошибок чтения: ${seeded.filesFailed})`
  );
} finally {
  fs.rmSync(path.dirname(tmpDb), { recursive: true, force: true });
}

const committed = JSON.parse(fs.readFileSync(committedPath, "utf8")) as {
  rules: Array<{ id: string }>;
};

const freshIds = new Set(freshRules.map((r) => r.id));
const committedIds = new Set(committed.rules.map((r) => r.id));

const missing = [...freshIds].filter((id) => !committedIds.has(id));
const extra = [...committedIds].filter((id) => !freshIds.has(id));

console.log(`В каталоге: ${committedIds.size} | из PDF: ${freshIds.size}`);

if (missing.length === 0 && extra.length === 0) {
  console.log("Каталог совпадает с нормативами — у всех будет одинаково.");
  process.exit(0);
}

console.error("\nКаталог разошёлся с нормативами:");
if (missing.length > 0) {
  console.error(`  нет в каталоге, но извлекается из PDF: ${missing.length}`);
  for (const id of missing.slice(0, 5)) console.error(`    ${id}`);
}
if (extra.length > 0) {
  console.error(`  есть в каталоге, но из PDF не извлекается: ${extra.length}`);
  for (const id of extra.slice(0, 5)) console.error(`    ${id}`);
}
console.error(
  "\nПочинить: npm run seed:norms -- --prune, затем закоммитить " +
    "plugin/Resources/norm-catalog.json.\n" +
    "Пока не починено — ассистент в Revit и проверки MCP работают по разным нормам."
);
process.exit(1);
