/**
 * Export SQLite norm library → plugin/Resources/norm-catalog.json
 * for the in-Revit AI assistant (offline query + check_* defaults).
 */
import { mkdir, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type DatabaseConstructor from "better-sqlite3";
import {
  loadEvacuationWidthRulesFromNormatives,
  pickPrimaryEvacuationWidthRule,
} from "./evacuationWidthRules.js";
import {
  loadMinDimensionRulesFromNormatives,
  resolveMinDimensionLimits,
} from "./minDimensionsRules.js";
import { resolveRoomDepthLimitFromLibrary } from "./normAudit/resolveDepthLimit.js";
import {
  getNormLibraryCounts,
  listAllNormRules,
  type StoredNormRule,
} from "./rulesStore.js";

type Database = DatabaseConstructor.Database;

const __dirname = dirname(fileURLToPath(import.meta.url));

/** server/src/normatives → repo root → plugin/Resources */
export const DEFAULT_NORM_CATALOG_PATH = join(
  __dirname,
  "..",
  "..",
  "..",
  "plugin",
  "Resources",
  "norm-catalog.json"
);

export interface NormCatalogSource {
  document: string;
  clause: string;
  quote: string;
  page?: number;
}

export interface NormCatalogRule {
  id: string;
  type: string;
  object: string;
  value: unknown;
  unit: string;
  normalized?: { min?: number; max?: number; exact?: number };
  tags: string[];
  source: NormCatalogSource;
}

export interface NormCatalogFile {
  exportedAt: string;
  ruleCount: number;
  documentCount: number;
  resolved: Record<string, Record<string, unknown>>;
  rules: NormCatalogRule[];
}

function compactSource(
  source:
    | { document?: string; clause?: string; quote?: string; page?: number }
    | null
    | undefined
): NormCatalogSource | null {
  if (!source?.document) return null;
  return {
    document: source.document,
    clause: source.clause ?? "",
    quote: source.quote ?? "",
    ...(source.page != null ? { page: source.page } : {}),
  };
}

function toCatalogRule(rule: StoredNormRule): NormCatalogRule {
  const source = compactSource(rule.source) ?? {
    document: "",
    clause: "",
    quote: "",
  };
  return {
    id: rule.id,
    type: rule.type,
    object: rule.object,
    value: rule.value,
    unit: rule.unit,
    ...(rule.normalized ? { normalized: rule.normalized } : {}),
    tags: rule.tags ?? [],
    source,
  };
}

async function buildResolved(
  database: Database
): Promise<Record<string, Record<string, unknown>>> {
  const resolved: Record<string, Record<string, unknown>> = {};

  try {
    const { rules } = await loadEvacuationWidthRulesFromNormatives({});
    const primary = pickPrimaryEvacuationWidthRule(rules);
    if (primary?.minWidthMm != null) {
      const source = compactSource(primary.source);
      resolved.check_evacuation_width = {
        minWidthMm: primary.minWidthMm,
        ...(source ? { source } : {}),
      };
    }
  } catch {
    /* plugin falls back to hardcoded */
  }

  const depth = resolveRoomDepthLimitFromLibrary(database);
  if (depth?.maxDepthMm != null) {
    const source = compactSource(depth.source);
    resolved.check_room_depth = {
      maxDepthMm: depth.maxDepthMm,
      roomScope: "living",
      ...(source ? { source } : {}),
    };
  }

  try {
    const { rules } = await loadMinDimensionRulesFromNormatives({});
    const limits = resolveMinDimensionLimits(rules, { housingType: "ordinary" });
    const applied = limits.appliedRules?.[0];
    const source = compactSource(applied?.source ?? null);
    const entry: Record<string, unknown> = {};
    if (limits.minBalconyWidthMm != null)
      entry.minBalconyWidthMm = limits.minBalconyWidthMm;
    if (limits.minLoggiaWidthMm != null)
      entry.minLoggiaWidthMm = limits.minLoggiaWidthMm;
    if (limits.minLoggiaDepthMm != null)
      entry.minLoggiaDepthMm = limits.minLoggiaDepthMm;
    if (limits.minFirePathOutdoorWidthMm != null)
      entry.minFirePathOutdoorWidthMm = limits.minFirePathOutdoorWidthMm;
    if (limits.minFirePierToOpeningMm != null)
      entry.minFirePierToOpeningMm = limits.minFirePierToOpeningMm;
    if (limits.minFirePierBetweenOpeningsMm != null)
      entry.minFirePierBetweenOpeningsMm = limits.minFirePierBetweenOpeningsMm;
    if (source) entry.source = source;
    if (Object.keys(entry).length > 0) {
      resolved.check_min_dimensions = entry;
    }
  } catch {
    /* keep empty */
  }

  return resolved;
}

export async function exportNormCatalog(
  database: Database,
  outPath: string = DEFAULT_NORM_CATALOG_PATH
): Promise<NormCatalogFile> {
  const counts = getNormLibraryCounts(database);
  const rules = listAllNormRules(database).map(toCatalogRule);
  const resolved = await buildResolved(database);

  const catalog: NormCatalogFile = {
    exportedAt: new Date().toISOString(),
    ruleCount: counts.ruleCount,
    documentCount: counts.documentCount,
    resolved,
    rules,
  };

  await mkdir(dirname(outPath), { recursive: true });
  await writeFile(outPath, JSON.stringify(catalog, null, 2), "utf8");
  return catalog;
}
