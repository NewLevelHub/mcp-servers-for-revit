import type DatabaseConstructor from "better-sqlite3";
import type { BriefRequirement, BriefRequirementType } from "./types.js";

type Database = DatabaseConstructor.Database;

/**
 * SQLite storage for extracted brief requirements (REV-182) — mirrors
 * normatives/rulesStore.ts's shape (same dedup-by-key, topic search idea) but
 * intentionally simpler: no tags, no multi-hundred-line relevance tuning,
 * because that tuning in rulesStore was earned over several tickets against
 * real normative text and real query complaints. This module has neither yet
 * (REV-182 shipped on general heuristics, no real ТЗ sample — see
 * extractRequirements.ts's own header) — the search below is honest, plain
 * substring matching, not a claim of the same precision.
 */

export interface StoredBriefRequirement extends BriefRequirement {
  requirementKey: string;
  documentVersion: string | null;
  createdAt: number;
  updatedAt: number;
}

export interface SaveBriefRequirementsOptions {
  documentVersion?: string;
}

export interface SavedRequirementInfo {
  requirementKey: string;
  action: "inserted" | "updated";
}

export interface SaveBriefRequirementsResult {
  inserted: number;
  updated: number;
  results: SavedRequirementInfo[];
}

export interface QueryBriefOptions {
  topic: string;
  document?: string;
  type?: BriefRequirementType;
  limit?: number;
}

export const DEFAULT_QUERY_LIMIT = 10;

interface BriefRequirementRow {
  requirement_key: string;
  document: string;
  document_norm: string;
  document_version: string | null;
  clause: string;
  requirement_type: string;
  object: string;
  value_json: string;
  unit: string;
  quote: string;
  page: number | null;
  search_text: string;
  created_at: number;
  updated_at: number;
}

const initializedDbs = new WeakSet<Database>();

export function ensureBriefSchema(db: Database): void {
  if (initializedDbs.has(db)) return;

  db.exec(`
    CREATE TABLE IF NOT EXISTS project_brief_requirements (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      requirement_key TEXT NOT NULL UNIQUE,
      document TEXT NOT NULL,
      document_norm TEXT NOT NULL,
      document_version TEXT,
      clause TEXT NOT NULL,
      requirement_type TEXT NOT NULL,
      object TEXT NOT NULL,
      value_json TEXT NOT NULL,
      unit TEXT NOT NULL,
      quote TEXT NOT NULL,
      page INTEGER,
      search_text TEXT NOT NULL,
      created_at INTEGER NOT NULL,
      updated_at INTEGER NOT NULL
    );
    CREATE INDEX IF NOT EXISTS idx_brief_requirements_document ON project_brief_requirements(document);
    CREATE INDEX IF NOT EXISTS idx_brief_requirements_object ON project_brief_requirements(object);
  `);

  initializedDbs.add(db);
}

function normalizeText(value: string): string {
  return value.toLowerCase().replace(/ё/g, "е").replace(/\s+/g, " ").trim();
}

export function buildRequirementKey(requirement: BriefRequirement): string {
  return [requirement.source.document, requirement.source.clause, requirement.object, requirement.type]
    .map(normalizeText)
    .join("|");
}

function buildSearchText(requirement: BriefRequirement): string {
  return normalizeText(
    [requirement.object, requirement.source.quote, requirement.source.clause, requirement.source.document]
      .filter(Boolean)
      .join(" ")
  );
}

/**
 * Crude stemming so a different Russian word form still matches — "студии"
 * must find a room stored as "студия". Same technique (and same reasoning)
 * as normatives/rulesStore.ts's own topicToTerms; caught missing here by
 * briefStore.test.ts before it shipped: a literal-word-only search for
 * "студии" found nothing, because the stored text only ever says "студия".
 */
function topicToTerms(topic: string): string[] {
  const words = normalizeText(topic)
    .split(/[^a-zа-я0-9.\-]+/)
    .filter(Boolean);

  const stems = words.map((word) => {
    if (word.length > 5) return word.slice(0, -2);
    if (word.length > 3) return word.slice(0, -1);
    return word;
  });

  return [...new Set(stems)];
}

function escapeLikeTerm(term: string): string {
  return term.replace(/[\\%_]/g, (match) => `\\${match}`);
}

export function saveBriefRequirements(
  db: Database,
  requirements: BriefRequirement[],
  options: SaveBriefRequirementsOptions = {}
): SaveBriefRequirementsResult {
  ensureBriefSchema(db);

  const selectStmt = db.prepare("SELECT id FROM project_brief_requirements WHERE requirement_key = ?");
  const insertStmt = db.prepare(`
    INSERT INTO project_brief_requirements (
      requirement_key, document, document_norm, document_version, clause, requirement_type,
      object, value_json, unit, quote, page, search_text, created_at, updated_at
    ) VALUES (
      @requirementKey, @document, @documentNorm, @documentVersion, @clause, @requirementType,
      @object, @valueJson, @unit, @quote, @page, @searchText, @now, @now
    )
  `);
  const updateStmt = db.prepare(`
    UPDATE project_brief_requirements SET
      document_version = COALESCE(@documentVersion, document_version),
      value_json = @valueJson,
      unit = @unit,
      quote = @quote,
      page = @page,
      search_text = @searchText,
      updated_at = @now
    WHERE requirement_key = @requirementKey
  `);

  const saveAll = db.transaction((items: BriefRequirement[]): SavedRequirementInfo[] => {
    const now = Date.now();
    return items.map((requirement) => {
      const requirementKey = buildRequirementKey(requirement);
      const existing = selectStmt.get(requirementKey) as { id: number } | undefined;

      const params = {
        requirementKey,
        document: requirement.source.document,
        documentNorm: normalizeText(requirement.source.document),
        documentVersion: options.documentVersion ?? null,
        clause: requirement.source.clause,
        requirementType: requirement.type,
        object: requirement.object,
        valueJson: JSON.stringify(requirement.value),
        unit: requirement.unit,
        quote: requirement.source.quote,
        page: requirement.source.page ?? null,
        searchText: buildSearchText(requirement),
        now,
      };

      if (existing) {
        updateStmt.run(params);
        return { requirementKey, action: "updated" as const };
      }
      insertStmt.run(params);
      return { requirementKey, action: "inserted" as const };
    });
  });

  const results = saveAll(requirements);
  return {
    inserted: results.filter((r) => r.action === "inserted").length,
    updated: results.filter((r) => r.action === "updated").length,
    results,
  };
}

function rowToStored(row: BriefRequirementRow): StoredBriefRequirement {
  return {
    id: row.requirement_key,
    requirementKey: row.requirement_key,
    type: row.requirement_type as BriefRequirementType,
    object: row.object,
    value: JSON.parse(row.value_json),
    unit: row.unit as BriefRequirement["unit"],
    source: {
      document: row.document,
      clause: row.clause,
      quote: row.quote,
      page: row.page ?? undefined,
    },
    documentVersion: row.document_version,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

export function queryBriefRequirements(
  db: Database,
  options: QueryBriefOptions
): StoredBriefRequirement[] {
  ensureBriefSchema(db);

  const terms = topicToTerms(options.topic);
  const params: unknown[] = [];
  const filters: string[] = [];

  if (terms.length > 0) {
    filters.push(`(${terms.map(() => "search_text LIKE ? ESCAPE '\\'").join(" OR ")})`);
    for (const term of terms) params.push(`%${escapeLikeTerm(term)}%`);
  }
  if (options.type) {
    filters.push("requirement_type = ?");
    params.push(options.type);
  }
  if (options.document) {
    filters.push("document_norm LIKE ? ESCAPE '\\'");
    params.push(`%${escapeLikeTerm(normalizeText(options.document))}%`);
  }

  const filterSql = filters.length > 0 ? `WHERE ${filters.join(" AND ")}` : "";
  const limit = options.limit ?? DEFAULT_QUERY_LIMIT;

  const rows = db
    .prepare(
      `SELECT * FROM project_brief_requirements ${filterSql}
       ORDER BY document, clause, object
       LIMIT ?`
    )
    .all(...params, limit) as BriefRequirementRow[];

  return rows.map(rowToStored);
}

/** All requirements of a given type for one document — the input to check_against_brief. */
export function listBriefRequirementsByType(
  db: Database,
  type: BriefRequirementType,
  document?: string
): StoredBriefRequirement[] {
  ensureBriefSchema(db);

  const filters = ["requirement_type = ?"];
  const params: unknown[] = [type];
  if (document) {
    filters.push("document_norm LIKE ? ESCAPE '\\'");
    params.push(`%${escapeLikeTerm(normalizeText(document))}%`);
  }

  const rows = db
    .prepare(
      `SELECT * FROM project_brief_requirements WHERE ${filters.join(" AND ")} ORDER BY document, object`
    )
    .all(...params) as BriefRequirementRow[];

  return rows.map(rowToStored);
}

export interface BriefLibraryStats {
  requirementCount: number;
  documentCount: number;
  documents: { document: string; requirementCount: number }[];
}

export function getBriefLibraryStats(db: Database): BriefLibraryStats {
  ensureBriefSchema(db);

  const totalRow = db.prepare("SELECT COUNT(*) AS c FROM project_brief_requirements").get() as { c: number };
  const docCountRow = db
    .prepare("SELECT COUNT(DISTINCT document) AS c FROM project_brief_requirements")
    .get() as { c: number };
  const docs = db
    .prepare(
      `SELECT document, COUNT(*) AS requirement_count FROM project_brief_requirements
       GROUP BY document ORDER BY requirement_count DESC, document`
    )
    .all() as Array<{ document: string; requirement_count: number }>;

  return {
    requirementCount: totalRow.c,
    documentCount: docCountRow.c,
    documents: docs.map((row) => ({ document: row.document, requirementCount: row.requirement_count })),
  };
}
