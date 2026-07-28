#!/usr/bin/env node
/**
 * Export norm library to plugin/Resources/norm-catalog.json.
 * Usage: npm run export:norm-catalog
 */
import db from "../database/db.js";
import {
  DEFAULT_NORM_CATALOG_PATH,
  exportNormCatalog,
} from "../normatives/exportNormCatalog.js";

const catalog = await exportNormCatalog(db);

console.log(
  JSON.stringify(
    {
      success: true,
      path: DEFAULT_NORM_CATALOG_PATH,
      ruleCount: catalog.ruleCount,
      documentCount: catalog.documentCount,
      resolvedKeys: Object.keys(catalog.resolved),
    },
    null,
    2
  )
);

process.exit(catalog.ruleCount > 0 ? 0 : 1);
