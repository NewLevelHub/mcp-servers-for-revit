import type DatabaseConstructor from "better-sqlite3";
import type {
  NormativeApplicability,
  NormativeNumericRange,
  NormativeRule,
  NormativeRuleType,
  NormativeRuleValue,
  NormativeUnit,
} from "./types.js";

type Database = DatabaseConstructor.Database;

/**
 * Rule to save. `tags` are semantic keywords the calling agent generates
 * (synonyms and translations, both Russian and Kazakh) — they feed the topic
 * search, so rules are found by meaning, not only by words from the quote.
 */
export interface SaveableNormRule extends NormativeRule {
  tags?: string[];
}

/** NormativeRule persisted in the local rules library (REV-30). */
export interface StoredNormRule extends NormativeRule {
  ruleKey: string;
  tags: string[];
  documentVersion: string | null;
  createdAt: number;
  updatedAt: number;
}

export interface SaveNormRulesOptions {
  /** Document edition/revision, e.g. "27.04.2021". */
  documentVersion?: string;
}

export interface SavedRuleInfo {
  ruleKey: string;
  action: "inserted" | "updated";
}

export interface SaveNormRulesResult {
  inserted: number;
  updated: number;
  results: SavedRuleInfo[];
}

export interface QueryNormRulesOptions {
  /** Topic in natural language, e.g. "ширина коридора". */
  topic: string;
  /** Optional document filter (case-insensitive substring), e.g. "СП РК 3.02-101". */
  document?: string;
  ruleType?: NormativeRuleType;
  limit?: number;
}

const DEFAULT_QUERY_LIMIT = 50;

interface NormRuleRow {
  rule_key: string;
  document: string;
  document_version: string | null;
  clause: string;
  rule_type: string;
  object: string;
  value_json: string;
  unit: string;
  applicability_json: string | null;
  normalized_json: string | null;
  quote: string;
  page: number | null;
  tags_json: string | null;
  created_at: number;
  updated_at: number;
}

const initializedDbs = new WeakSet<Database>();

/**
 * Creates the norm_rules table. Storage lives in the shared SQLite database
 * (revit-data.db), independent from the /normatives folder with sample PDFs.
 */
export function ensureNormRulesSchema(db: Database): void {
  if (initializedDbs.has(db)) return;

  db.exec(`
    CREATE TABLE IF NOT EXISTS norm_rules (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      rule_key TEXT NOT NULL UNIQUE,
      document TEXT NOT NULL,
      document_norm TEXT NOT NULL,
      document_version TEXT,
      clause TEXT NOT NULL,
      rule_type TEXT NOT NULL,
      object TEXT NOT NULL,
      value_json TEXT NOT NULL,
      unit TEXT NOT NULL,
      applicability_json TEXT,
      normalized_json TEXT,
      quote TEXT NOT NULL,
      page INTEGER,
      tags_json TEXT,
      search_text TEXT NOT NULL,
      created_at INTEGER NOT NULL,
      updated_at INTEGER NOT NULL
    );
    CREATE INDEX IF NOT EXISTS idx_norm_rules_document ON norm_rules(document);
    CREATE INDEX IF NOT EXISTS idx_norm_rules_rule_type ON norm_rules(rule_type);
  `);

  // Migrate tables created before the tags column existed.
  const columns = db.prepare("PRAGMA table_info(norm_rules)").all() as Array<{
    name: string;
  }>;
  if (!columns.some((column) => column.name === "tags_json")) {
    db.exec("ALTER TABLE norm_rules ADD COLUMN tags_json TEXT");
  }

  initializedDbs.add(db);
}

/** Lowercase + ё→е + collapse whitespace, so keys/search are form-insensitive. */
function normalizeText(value: string): string {
  return value.toLowerCase().replace(/ё/g, "е").replace(/\s+/g, " ").trim();
}

/**
 * Deduplication key. One clause often contains several rules (e.g. corridor
 * width and ceiling height in the same п. 4.2.3), so object and rule type are
 * part of the key — dedup by document+clause alone would drop rules.
 */
export function buildRuleKey(rule: NormativeRule): string {
  return [rule.source.document, rule.source.clause, rule.object, rule.type]
    .map(normalizeText)
    .join("|");
}

/**
 * SQLite's built-in lower()/LIKE are case-insensitive for ASCII only, so
 * Cyrillic matching is done against this JS-lowercased column.
 */
function buildSearchText(rule: SaveableNormRule): string {
  const parts = [
    rule.object,
    ...(rule.tags ?? []),
    rule.source.quote,
    rule.source.clause,
    rule.source.document,
    rule.unit,
    rule.applicability?.raw,
    rule.applicability?.buildingType,
    rule.applicability?.roomType,
    ...(rule.applicability?.conditions ?? []),
  ];
  return normalizeText(parts.filter(Boolean).join(" "));
}

/**
 * Splits a topic into crude word stems: "ширина коридора" → ["шири", "коридо"],
 * so different Russian word forms (коридор/коридора/коридорах) still match.
 */
function topicToTerms(topic: string): string[] {
  const words = normalizeText(topic)
    .split(/[^a-zа-я0-9.\-]+/)
    .filter(Boolean);

  return words.map((word) => {
    if (word.length > 5) return word.slice(0, -2);
    if (word.length > 3) return word.slice(0, -1);
    return word;
  });
}

function escapeLikeTerm(term: string): string {
  return term.replace(/[\\%_]/g, (match) => `\\${match}`);
}

/** Default semantic tags when the agent omits them on save_norm_rule. */
export function suggestRuleTags(rule: NormativeRule): string[] {
  const tags = new Set<string>([rule.object, rule.source.document]);
  const text = `${rule.object} ${rule.source.quote}`.toLowerCase();

  const add = (...items: string[]) => {
    for (const item of items) tags.add(item);
  };

  if (/надпис|штамп/i.test(text)) {
    add("основная надпись", "штамп", "штамп чертежа", "басты жазу", "мөр");
  }
  if (/коридор|дәліз/i.test(text)) {
    add("коридор", "ширина коридора", "проход", "дәліз", "дәліз ені");
  }
  if (/двер|есік/i.test(text)) add("дверь", "дверной проём", "есік");
  if (/площад|алаң|аудан/i.test(text)) add("площадь", "алаң", "ауданы");
  if (/ширин|ені/i.test(text)) add("ширина", "ені");
  if (/высот|биікт/i.test(text)) add("высота", "биіктігі");
  if (/жил|тұрғын/i.test(text)) add("жилое помещение", "тұрғын үй-жай");
  if (/эвакуац/i.test(text)) add("эвакуация", "эвакуационный коридор");

  return [...tags].slice(0, 8);
}

export function withSuggestedTags(rules: SaveableNormRule[]): SaveableNormRule[] {
  return rules.map((rule) => ({
    ...rule,
    tags: rule.tags?.length ? rule.tags : suggestRuleTags(rule),
  }));
}

export function saveNormRules(
  db: Database,
  rules: SaveableNormRule[],
  options: SaveNormRulesOptions = {}
): SaveNormRulesResult {
  ensureNormRulesSchema(db);

  const selectStmt = db.prepare(
    "SELECT id, tags_json FROM norm_rules WHERE rule_key = ?"
  );
  const insertStmt = db.prepare(`
    INSERT INTO norm_rules (
      rule_key, document, document_norm, document_version, clause, rule_type,
      object, value_json, unit, applicability_json, normalized_json, quote,
      page, tags_json, search_text, created_at, updated_at
    ) VALUES (
      @ruleKey, @document, @documentNorm, @documentVersion, @clause, @ruleType,
      @object, @valueJson, @unit, @applicabilityJson, @normalizedJson, @quote,
      @page, @tagsJson, @searchText, @now, @now
    )
  `);
  const updateStmt = db.prepare(`
    UPDATE norm_rules SET
      document_version = COALESCE(@documentVersion, document_version),
      value_json = @valueJson,
      unit = @unit,
      applicability_json = @applicabilityJson,
      normalized_json = @normalizedJson,
      quote = @quote,
      page = @page,
      tags_json = @tagsJson,
      search_text = @searchText,
      updated_at = @now
    WHERE rule_key = @ruleKey
  `);

  const saveAll = db.transaction((items: SaveableNormRule[]): SavedRuleInfo[] => {
    const now = Date.now();
    return items.map((rule) => {
      const ruleKey = buildRuleKey(rule);
      const existing = selectStmt.get(ruleKey) as
        | { id: number; tags_json: string | null }
        | undefined;

      // Re-saving without tags must not lose previously saved tags
      // (and must keep them searchable via search_text).
      const tags =
        rule.tags ??
        (existing?.tags_json
          ? (JSON.parse(existing.tags_json) as string[])
          : undefined);

      const params = {
        ruleKey,
        document: rule.source.document,
        documentNorm: normalizeText(rule.source.document),
        documentVersion: options.documentVersion ?? null,
        clause: rule.source.clause,
        ruleType: rule.type,
        object: rule.object,
        valueJson: JSON.stringify(rule.value),
        unit: rule.unit,
        applicabilityJson: rule.applicability
          ? JSON.stringify(rule.applicability)
          : null,
        normalizedJson: rule.normalized
          ? JSON.stringify(rule.normalized)
          : null,
        quote: rule.source.quote,
        page: rule.source.page ?? null,
        tagsJson: tags && tags.length > 0 ? JSON.stringify(tags) : null,
        searchText: buildSearchText({ ...rule, tags }),
        now,
      };

      if (existing) {
        updateStmt.run(params);
        return { ruleKey: params.ruleKey, action: "updated" as const };
      }
      insertStmt.run(params);
      return { ruleKey: params.ruleKey, action: "inserted" as const };
    });
  });

  const results = saveAll(rules);
  return {
    inserted: results.filter((r) => r.action === "inserted").length,
    updated: results.filter((r) => r.action === "updated").length,
    results,
  };
}

function rowToStoredRule(row: NormRuleRow): StoredNormRule {
  return {
    id: row.rule_key,
    ruleKey: row.rule_key,
    tags: row.tags_json ? (JSON.parse(row.tags_json) as string[]) : [],
    type: row.rule_type as NormativeRuleType,
    object: row.object,
    value: JSON.parse(row.value_json) as NormativeRuleValue,
    unit: row.unit as NormativeUnit,
    applicability: row.applicability_json
      ? (JSON.parse(row.applicability_json) as NormativeApplicability)
      : null,
    normalized: row.normalized_json
      ? (JSON.parse(row.normalized_json) as NormativeNumericRange)
      : undefined,
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

export function queryNormRules(
  db: Database,
  options: QueryNormRulesOptions
): StoredNormRule[] {
  ensureNormRulesSchema(db);

  const terms = topicToTerms(options.topic);
  const params: unknown[] = [];

  // Rules match if ANY topic term is found; more matched terms rank higher.
  // Requiring ALL terms is too strict: e.g. a rule tagged object="коридор"
  // with a Kazakh-language quote would never match "ширина коридора".
  const scoreExpr =
    terms.length > 0
      ? terms
          .map(() => `(search_text LIKE ? ESCAPE '\\')`)
          .join(" + ")
      : "1";
  for (const term of terms) {
    params.push(`%${escapeLikeTerm(term)}%`);
  }

  const filters: string[] = [];
  if (options.ruleType) {
    filters.push("rule_type = ?");
    params.push(options.ruleType);
  }
  if (options.document) {
    filters.push(`document_norm LIKE ? ESCAPE '\\'`);
    params.push(`%${escapeLikeTerm(normalizeText(options.document))}%`);
  }
  const filterSql = filters.length > 0 ? `WHERE ${filters.join(" AND ")}` : "";
  const limit = options.limit ?? DEFAULT_QUERY_LIMIT;

  const rows = db
    .prepare(
      `SELECT * FROM (
         SELECT *, (${scoreExpr}) AS match_score FROM norm_rules ${filterSql}
       )
       WHERE match_score > 0
       ORDER BY match_score DESC, document, clause
       LIMIT ?`
    )
    .all(...params, limit) as NormRuleRow[];

  return rows.map(rowToStoredRule);
}
