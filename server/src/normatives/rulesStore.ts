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

/**
 * Default MCP/query limit. High values (50+) return huge quotes and make the
 * Cursor agent slow to answer — keep small; agent can pass limit when needed.
 */
export const DEFAULT_QUERY_LIMIT = 5;

/** Cap quote/applicability length in MCP text payloads (full text stays in DB). */
export const MAX_QUOTE_CHARS_IN_MCP = 480;

/** Slim rule shape for MCP responses (faster for the agent / less tokens). */
export interface CompactNormRuleForMcp {
  id: string;
  type: NormativeRuleType;
  object: string;
  value: NormativeRuleValue;
  unit: NormativeUnit;
  applicability?: { raw: string; roomType?: string; buildingType?: string };
  normalized?: NormativeNumericRange;
  source: {
    document: string;
    clause: string;
    quote: string;
    page?: number;
    quoteTruncated?: boolean;
  };
  documentVersion?: string | null;
}

function truncateText(
  text: string,
  maxChars: number
): { text: string; truncated: boolean } {
  const trimmed = text.trim();
  if (trimmed.length <= maxChars) {
    return { text: trimmed, truncated: false };
  }
  return {
    text: `${trimmed.slice(0, Math.max(0, maxChars - 1)).trimEnd()}…`,
    truncated: true,
  };
}

/**
 * Strip bulky fields and truncate quotes for MCP JSON. DB / in-memory rules
 * stay full — only the wire payload shrinks.
 */
export function compactRulesForMcp(
  rules: StoredNormRule[],
  maxQuoteChars: number = MAX_QUOTE_CHARS_IN_MCP
): CompactNormRuleForMcp[] {
  return rules.map((rule) => {
    const quote = truncateText(rule.source.quote, maxQuoteChars);
    const value =
      typeof rule.value === "string"
        ? truncateText(rule.value, maxQuoteChars).text
        : rule.value;

    const compact: CompactNormRuleForMcp = {
      id: rule.id,
      type: rule.type,
      object: rule.object,
      value,
      unit: rule.unit,
      source: {
        document: rule.source.document,
        clause: rule.source.clause,
        quote: quote.text,
        ...(rule.source.page != null ? { page: rule.source.page } : {}),
        ...(quote.truncated ? { quoteTruncated: true } : {}),
      },
    };

    if (rule.normalized) {
      compact.normalized = rule.normalized;
    }
    if (rule.documentVersion) {
      compact.documentVersion = rule.documentVersion;
    }
    if (rule.applicability?.raw) {
      const raw = truncateText(rule.applicability.raw, maxQuoteChars);
      compact.applicability = {
        raw: raw.text,
        ...(rule.applicability.roomType
          ? { roomType: rule.applicability.roomType }
          : {}),
        ...(rule.applicability.buildingType
          ? { buildingType: rule.applicability.buildingType }
          : {}),
      };
    }

    return compact;
  });
}

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

  const stems = words.map((word) => {
    if (word.length > 5) return word.slice(0, -2);
    if (word.length > 3) return word.slice(0, -1);
    return word;
  });

  // Cross-language / synonym expansion so RU topics hit KZ quotes (REV-46).
  const extras: string[] = [];
  const joined = stems.join(" ");
  if (/надпис|штамп/i.test(joined)) {
    extras.push("основная надпись", "штамп", "басты жазу");
  }
  if (/ширин|width/.test(joined)) extras.push("ені", "еніні");
  if (/коридор|дәліз/.test(joined)) extras.push("дәліз", "коридор");
  if (/эвакуац/.test(joined)) extras.push("эвакуац", "эвакуациял");
  if (/лоджи|балкон/.test(joined)) extras.push("лоджи", "балкон");

  return [...new Set([...stems, ...extras])];
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

const NUMERIC_RULE_TYPES = new Set<NormativeRuleType>([
  "min_value",
  "max_value",
  "range",
  "exact_value",
]);

const DIMENSIONAL_TOPIC_RE =
  /ширин|высот|глубин|площад|ені|эвакуац|коридор|дәліз|не\s+менее|мин\.?|max|min/i;

const DEFINITION_QUOTE_RE =
  /световой\s+карман|жарық\s+қалта|называется|это\s*:|определяется\s+как/i;

/** Rich relevance score so definition hits lose to numeric width rules (REV-46). */
export function scoreRuleAgainstTopic(
  rule: Pick<
    StoredNormRule,
    "object" | "type" | "tags" | "normalized" | "source"
  >,
  topic: string
): number {
  const terms = topicToTerms(topic);
  if (terms.length === 0) return 0;

  const topicNorm = normalizeText(topic);
  const objectNorm = normalizeText(rule.object);
  const tagsNorm = normalizeText((rule.tags ?? []).join(" "));
  const quoteNorm = normalizeText(rule.source.quote);
  const clauseNorm = normalizeText(rule.source.clause);
  const documentNorm = normalizeText(rule.source.document);
  const objectTags = `${objectNorm} ${tagsNorm}`.trim();
  const allText = `${objectTags} ${quoteNorm} ${clauseNorm} ${documentNorm}`;

  let score = 0;
  for (const term of terms) {
    if (objectNorm.includes(term)) score += 4;
    else if (tagsNorm.includes(term)) score += 3;
    else if (clauseNorm.includes(term)) score += 2;
    else if (quoteNorm.includes(term)) score += 1;
    else if (documentNorm.includes(term)) score += 1;
  }

  if (
    topicNorm.length > 4 &&
    (objectTags.includes(topicNorm) ||
      quoteNorm.includes(topicNorm) ||
      allText.includes(topicNorm))
  ) {
    score += 4;
  }

  const dimensional = DIMENSIONAL_TOPIC_RE.test(topic);
  if (dimensional && NUMERIC_RULE_TYPES.has(rule.type)) {
    score += 5;
  }
  if (
    dimensional &&
    rule.normalized &&
    (rule.normalized.min != null ||
      rule.normalized.max != null ||
      rule.normalized.exact != null)
  ) {
    score += 2;
  }
  if (dimensional && DEFINITION_QUOTE_RE.test(rule.source.quote)) {
    score -= 8;
  }
  if (dimensional && (rule.type === "note" || rule.type === "requirement")) {
    // Prefer quantifiable limits when the user asked for a dimension.
    score -= 2;
  }

  // Topic asks for WIDTH → prefer quotes about width, demote height/doors-only.
  const wantsWidth = /ширин|ені/i.test(topic);
  const wantsHeight = /высот|биікт/i.test(topic);
  const quoteHasWidth = /ширин|ені|width/i.test(rule.source.quote);
  const quoteHasHeight = /высот|биікт|height/i.test(rule.source.quote);
  const quoteHasEgress =
    /эвакуац|эвак\.|коридор|дәліз|проход|жол/i.test(rule.source.quote) ||
    /эвакуац|коридор|дәліз/i.test(rule.object);
  if (wantsWidth && quoteHasWidth) score += 6;
  if (wantsWidth && quoteHasHeight && !quoteHasWidth) score -= 5;
  if (/эвакуац|коридор|дәліз/i.test(topic) && quoteHasEgress) score += 4;
  if (
    wantsWidth &&
    /есік|дверн|дверь|door/i.test(rule.source.quote) &&
    !quoteHasWidth
  ) {
    score -= 4;
  }
  if (wantsHeight && quoteHasHeight) score += 4;

  // Topic: title block / штамп (questionnaire 1.4 example).
  const wantsTitleBlock = /основн.*надпис|штамп|басты жазу|мөр/i.test(topic);
  if (wantsTitleBlock) {
    if (
      /основн.*надпис|штамп|высот.*строк|басты жазу/i.test(
        `${rule.object} ${tagsNorm} ${quoteNorm}`
      )
    ) {
      score += 14;
    }
    if (/банкомат|дисплее|кровл|нахлест|нахлёст|указател/i.test(quoteNorm)) {
      score -= 12;
    }
  }

  // Prefer a clear single limit over nonsense wide ranges (e.g. 1–20 m).
  const hasSpan =
    rule.normalized?.min != null &&
    rule.normalized?.max != null &&
    rule.normalized.max - rule.normalized.min > 5000;
  if (hasSpan) score -= 8;

  // Plausible corridor clear widths are ~0.8–3.5 m (800–3500 mm).
  const mm =
    rule.normalized?.min ??
    rule.normalized?.exact ??
    rule.normalized?.max ??
    null;
  if (wantsWidth && quoteHasEgress && quoteHasWidth && mm != null && mm >= 800 && mm <= 3500) {
    score += 8;
  } else if (
    wantsWidth &&
    quoteHasEgress &&
    mm != null &&
    mm >= 800 &&
    mm <= 3500
  ) {
    score += 3;
  }
  if (wantsWidth && mm != null && (mm < 400 || mm > 6000)) {
    score -= 4;
  }

  // Strong phrase: width + corridor/egress in the same quote.
  if (
    wantsWidth &&
    quoteHasWidth &&
    /коридор|дәліз|эвакуац/i.test(rule.source.quote)
  ) {
    score += 10;
  }

  // Topic is corridor → demote doors/foyers tagged as corridor by bad extract.
  if (
    /коридор|дәліз|эвакуац/i.test(topic) &&
    (/^двер|^есік|дверь|door/i.test(rule.object) ||
      (/есік|дверн|фойе|вестибюл/i.test(rule.source.quote) &&
        !/коридор|дәліз/i.test(rule.source.quote)))
  ) {
    score -= 12;
  }

  // Prefer RU/KZ corridor wording over junk room-list extracts.
  if (
    wantsWidth &&
    /ширину\s+коридор|ширина\s+коридор|дәліздер\s+үшін\s+кемінде|ені\s+ортақ\s+дәліз/i.test(
      rule.source.quote
    )
  ) {
    score += 15;
  }

  return score;
}

export interface NormLibraryDocumentStat {
  document: string;
  ruleCount: number;
  versions: string[];
}

export interface NormLibraryStats {
  ruleCount: number;
  documentCount: number;
  documents: NormLibraryDocumentStat[];
}

/** Counts only — use on query path so every search does not GROUP BY all docs. */
export function getNormLibraryCounts(db: Database): {
  ruleCount: number;
  documentCount: number;
} {
  ensureNormRulesSchema(db);
  const ruleCountRow = db
    .prepare("SELECT COUNT(*) AS c FROM norm_rules")
    .get() as { c: number };
  const documentCountRow = db
    .prepare("SELECT COUNT(DISTINCT document) AS c FROM norm_rules")
    .get() as { c: number };
  return {
    ruleCount: ruleCountRow.c,
    documentCount: documentCountRow.c,
  };
}

export function getNormLibraryStats(db: Database): NormLibraryStats {
  ensureNormRulesSchema(db);

  const { ruleCount, documentCount } = getNormLibraryCounts(db);
  const docs = db
    .prepare(
      `SELECT document,
              COUNT(*) AS rule_count,
              GROUP_CONCAT(DISTINCT document_version) AS versions
       FROM norm_rules
       GROUP BY document
       ORDER BY rule_count DESC, document`
    )
    .all() as Array<{
    document: string;
    rule_count: number;
    versions: string | null;
  }>;

  return {
    ruleCount,
    documentCount,
    documents: docs.map((row) => ({
      document: row.document,
      ruleCount: row.rule_count,
      versions: (row.versions ?? "")
        .split(",")
        .map((v) => v.trim())
        .filter(Boolean),
    })),
  };
}

export function queryNormRules(
  db: Database,
  options: QueryNormRulesOptions
): StoredNormRule[] {
  ensureNormRulesSchema(db);

  const terms = topicToTerms(options.topic);
  const params: unknown[] = [];

  // Candidate filter: ANY topic term in search_text (broad recall).
  // Final order uses scoreRuleAgainstTopic (precision for customer topics).
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
  // Pull a wider candidate pool, then re-rank in JS.
  const candidateLimit = Math.max(limit * 8, 120);

  const rows = db
    .prepare(
      `SELECT * FROM (
         SELECT *, (${scoreExpr}) AS match_score FROM norm_rules ${filterSql}
       )
       WHERE match_score > 0
       ORDER BY match_score DESC, document, clause
       LIMIT ?`
    )
    .all(...params, candidateLimit) as NormRuleRow[];

  return rows
    .map(rowToStoredRule)
    .map((rule) => ({
      rule,
      score: scoreRuleAgainstTopic(rule, options.topic),
    }))
    .filter((item) => item.score > 0)
    .sort(
      (a, b) =>
        b.score - a.score ||
        a.rule.source.document.localeCompare(b.rule.source.document) ||
        a.rule.source.clause.localeCompare(b.rule.source.clause)
    )
    .slice(0, limit)
    .map((item) => item.rule);
}
