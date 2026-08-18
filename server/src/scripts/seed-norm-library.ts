#!/usr/bin/env node
/**
 * Seed SQLite norm library from repo/normatives/*.pdf (idempotent upsert).
 * Usage: npm run seed:norms
 */
import db from "../database/db.js";
import { seedNormLibrary } from "../normatives/seedLibrary.js";

const result = await seedNormLibrary(db, {
  // Unset = every page. Set SEED_NORM_MAX_PAGES only to cut a slow run short;
  // it drops rules from the end of every long document (REV-51).
  ...(process.env.SEED_NORM_MAX_PAGES
    ? { maxPages: Number(process.env.SEED_NORM_MAX_PAGES) }
    : {}),
});

console.log(
  JSON.stringify(
    {
      success: true,
      normativesDir: result.normativesDir,
      filesProcessed: result.filesProcessed,
      filesFailed: result.filesFailed,
      inserted: result.inserted,
      updated: result.updated,
      library: result.library,
      failures: result.files.filter((f) => f.error),
      emptyExtracts: result.files.filter((f) => f.skipped && !f.error),
    },
    null,
    2
  )
);

process.exit(result.filesFailed > 0 ? 1 : 0);
