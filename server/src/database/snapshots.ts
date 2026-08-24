import type { Database } from "better-sqlite3";
import type { SnapshotElementRow } from "../utils/modelSnapshot.js";

/**
 * `model_snapshots` and `snapshot_elements` — where a snapshot of the model goes
 * (REV-170), and the ground every later comparison stands on.
 *
 * ## Why SQLite and not a file in the profile
 *
 * The ribbon catalog (REV-151) is a file: it is small and it is read whole. This
 * is the opposite. Hundreds of thousands of rows, several snapshots of the same
 * model side by side, and the questions asked of them are joins — "what does this
 * snapshot have that the previous one does not". That is what the database
 * already wired into this server is for. The decision is written down in the
 * ticket so it does not get re-argued at review.
 *
 * ## Why a re-run cannot duplicate anything
 *
 * Two locks, at two levels. A snapshot is unique on (model, label): running the
 * same выдача twice replaces it rather than laying a second copy beside it. And
 * an element is unique on (snapshot, uniqueId), so a page that arrives twice —
 * a retry, an overlapping offset — overwrites its own rows instead of doubling
 * them.
 */

const initialized = new WeakSet<Database>();

export function ensureSnapshotSchema(db: Database): void {
  if (initialized.has(db)) return;

  db.exec(`
    CREATE TABLE IF NOT EXISTS model_snapshots (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      model_name TEXT NOT NULL,
      model_path TEXT,
      label TEXT NOT NULL,
      note TEXT,
      taken_at INTEGER NOT NULL,
      element_count INTEGER NOT NULL DEFAULT 0,
      duration_ms INTEGER,
      revit_version TEXT,
      parameter_labels TEXT,
      status TEXT NOT NULL DEFAULT 'building',
      UNIQUE(model_name, label)
    );

    CREATE TABLE IF NOT EXISTS snapshot_elements (
      snapshot_id INTEGER NOT NULL,
      unique_id TEXT NOT NULL,
      element_id INTEGER NOT NULL,
      category_key TEXT NOT NULL DEFAULT '',
      category TEXT NOT NULL DEFAULT '',
      family_name TEXT NOT NULL DEFAULT '',
      type_name TEXT NOT NULL DEFAULT '',
      type_id INTEGER,
      level_name TEXT NOT NULL DEFAULT '',
      room_name TEXT NOT NULL DEFAULT '',
      room_number TEXT NOT NULL DEFAULT '',
      bbox_min_x REAL, bbox_min_y REAL, bbox_min_z REAL,
      bbox_max_x REAL, bbox_max_y REAL, bbox_max_z REAL,
      param_hash TEXT NOT NULL,
      params_json TEXT NOT NULL DEFAULT '{}',
      PRIMARY KEY (snapshot_id, unique_id),
      FOREIGN KEY (snapshot_id) REFERENCES model_snapshots(id) ON DELETE CASCADE
    ) WITHOUT ROWID;

    CREATE INDEX IF NOT EXISTS idx_snapshots_model ON model_snapshots(model_name, taken_at);
    CREATE INDEX IF NOT EXISTS idx_snapshot_elements_category
      ON snapshot_elements(snapshot_id, category_key);
    CREATE INDEX IF NOT EXISTS idx_snapshot_elements_level
      ON snapshot_elements(snapshot_id, level_name);
  `);

  initialized.add(db);
}

export interface SnapshotHeader {
  id: number;
  modelName: string;
  modelPath: string | null;
  label: string;
  note: string | null;
  takenAt: number;
  elementCount: number;
  durationMs: number | null;
  revitVersion: string | null;
  status: string;
}

interface SnapshotRow {
  id: number;
  model_name: string;
  model_path: string | null;
  label: string;
  note: string | null;
  taken_at: number;
  element_count: number;
  duration_ms: number | null;
  revit_version: string | null;
  status: string;
}

function toHeader(row: SnapshotRow): SnapshotHeader {
  return {
    id: row.id,
    modelName: row.model_name,
    modelPath: row.model_path,
    label: row.label,
    note: row.note,
    takenAt: row.taken_at,
    elementCount: row.element_count,
    durationMs: row.duration_ms,
    revitVersion: row.revit_version,
    status: row.status,
  };
}

/**
 * Open a snapshot for writing, replacing any snapshot of the same model under the
 * same label.
 *
 * The replacement is a delete, not an update: half of yesterday's «выдача АР» left
 * under today's rows would be a snapshot of a model that never existed, and the
 * diff built on it would be fiction. It is left `status = 'building'` until the
 * last page lands, so an interrupted run is visibly incomplete rather than
 * quietly short.
 */
export function beginSnapshot(
  db: Database,
  input: {
    modelName: string;
    modelPath?: string;
    label: string;
    note?: string;
    revitVersion?: string;
  }
): { id: number; replaced: boolean } {
  ensureSnapshotSchema(db);

  const existing = db
    .prepare("SELECT id FROM model_snapshots WHERE model_name = ? AND label = ?")
    .get(input.modelName, input.label) as { id: number } | undefined;

  if (existing) deleteSnapshot(db, existing.id);

  const result = db
    .prepare(
      `INSERT INTO model_snapshots
         (model_name, model_path, label, note, taken_at, element_count, revit_version, status)
       VALUES (?, ?, ?, ?, ?, 0, ?, 'building')`
    )
    .run(
      input.modelName,
      input.modelPath ?? null,
      input.label,
      input.note ?? null,
      Date.now(),
      input.revitVersion ?? null
    );

  return { id: Number(result.lastInsertRowid), replaced: Boolean(existing) };
}

/**
 * Write one page.
 *
 * One transaction per page, not per row and not per snapshot: per row, SQLite
 * fsyncs on every element and a 300k model takes the half hour the ticket rules
 * out; per snapshot, an interrupted run rolls back everything already read.
 *
 * `INSERT OR REPLACE` is the second half of "повторный запуск не дублирует
 * записи" — a page delivered twice overwrites itself.
 */
export function insertSnapshotElements(
  db: Database,
  snapshotId: number,
  rows: SnapshotElementRow[]
): number {
  if (rows.length === 0) return 0;

  const insert = db.prepare(
    `INSERT OR REPLACE INTO snapshot_elements (
       snapshot_id, unique_id, element_id, category_key, category,
       family_name, type_name, type_id, level_name, room_name, room_number,
       bbox_min_x, bbox_min_y, bbox_min_z, bbox_max_x, bbox_max_y, bbox_max_z,
       param_hash, params_json
     ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
  );

  const writePage = db.transaction((batch: SnapshotElementRow[]) => {
    for (const row of batch) {
      insert.run(
        snapshotId,
        row.uniqueId,
        row.elementId,
        row.categoryKey,
        row.category,
        row.familyName,
        row.typeName,
        row.typeId,
        row.levelName,
        row.roomName,
        row.roomNumber,
        row.bboxMinX,
        row.bboxMinY,
        row.bboxMinZ,
        row.bboxMaxX,
        row.bboxMaxY,
        row.bboxMaxZ,
        row.paramHash,
        row.paramsJson
      );
    }
  });

  writePage(rows);
  return rows.length;
}

/**
 * Close a snapshot: count what actually landed and record how it ended.
 *
 * `status` is `partial` when the read did not finish. It matters: a partial
 * snapshot compares as though everything missing from it had been added since,
 * so whatever reads it later has to be able to see that it is short — the count
 * alone does not say so, because nobody remembers how many elements the model
 * had that day.
 */
export function finishSnapshot(
  db: Database,
  snapshotId: number,
  input: {
    durationMs: number;
    parameterLabels?: Record<string, string>;
    status?: "ready" | "partial";
  }
): number {
  const counted = db
    .prepare("SELECT COUNT(*) AS n FROM snapshot_elements WHERE snapshot_id = ?")
    .get(snapshotId) as { n: number };

  db.prepare(
    `UPDATE model_snapshots
        SET element_count = ?, duration_ms = ?, parameter_labels = ?, status = ?
      WHERE id = ?`
  ).run(
    counted.n,
    Math.round(input.durationMs),
    input.parameterLabels ? JSON.stringify(input.parameterLabels) : null,
    input.status ?? "ready",
    snapshotId
  );

  return counted.n;
}

export function listSnapshots(db: Database, modelName?: string): SnapshotHeader[] {
  ensureSnapshotSchema(db);

  const rows = modelName
    ? (db
        .prepare(
          "SELECT * FROM model_snapshots WHERE model_name = ? ORDER BY taken_at DESC"
        )
        .all(modelName) as SnapshotRow[])
    : (db
        .prepare("SELECT * FROM model_snapshots ORDER BY taken_at DESC")
        .all() as SnapshotRow[]);

  return rows.map(toHeader);
}

export function getSnapshot(db: Database, snapshotId: number): SnapshotHeader | null {
  ensureSnapshotSchema(db);

  const row = db
    .prepare("SELECT * FROM model_snapshots WHERE id = ?")
    .get(snapshotId) as SnapshotRow | undefined;

  return row ? toHeader(row) : null;
}

export function findSnapshotByLabel(
  db: Database,
  modelName: string,
  label: string
): SnapshotHeader | null {
  ensureSnapshotSchema(db);

  const row = db
    .prepare("SELECT * FROM model_snapshots WHERE model_name = ? AND label = ?")
    .get(modelName, label) as SnapshotRow | undefined;

  return row ? toHeader(row) : null;
}

/**
 * Delete a snapshot and its elements.
 *
 * The elements go first and explicitly. `ON DELETE CASCADE` is declared, and
 * `db.ts` does turn foreign keys on — but a snapshot whose header is gone and
 * whose 300k rows are not is a leak nobody would ever notice, so this does not
 * depend on the pragma surviving a future edit.
 */
export function deleteSnapshot(db: Database, snapshotId: number): boolean {
  ensureSnapshotSchema(db);

  const remove = db.transaction((id: number) => {
    db.prepare("DELETE FROM snapshot_elements WHERE snapshot_id = ?").run(id);
    return db.prepare("DELETE FROM model_snapshots WHERE id = ?").run(id).changes;
  });

  return remove(snapshotId) > 0;
}

/**
 * Keep the newest `keep` snapshots of a model and delete the rest — the answer to
 * "база не растёт бесконечно". A snapshot of a large model is a few hundred
 * megabytes; without this, every выдача would be kept until the disk said no.
 */
export function pruneSnapshots(db: Database, modelName: string, keep: number): SnapshotHeader[] {
  ensureSnapshotSchema(db);

  if (keep <= 0) return [];

  const stale = listSnapshots(db, modelName).slice(keep);
  for (const snapshot of stale) deleteSnapshot(db, snapshot.id);
  return stale;
}

export interface SnapshotBreakdownRow {
  key: string;
  count: number;
}

/** Top categories of a snapshot — what an architect looks at first to see it is the right model. */
export function snapshotCategoryBreakdown(
  db: Database,
  snapshotId: number,
  limit = 10
): SnapshotBreakdownRow[] {
  ensureSnapshotSchema(db);

  return db
    .prepare(
      `SELECT CASE WHEN category = '' THEN category_key ELSE category END AS key,
              COUNT(*) AS count
         FROM snapshot_elements
        WHERE snapshot_id = ?
        GROUP BY key
        ORDER BY count DESC
        LIMIT ?`
    )
    .all(snapshotId, limit) as SnapshotBreakdownRow[];
}

interface SnapshotElementDbRow {
  unique_id: string;
  element_id: number;
  category_key: string;
  category: string;
  family_name: string;
  type_name: string;
  type_id: number | null;
  level_name: string;
  room_name: string;
  room_number: string;
  bbox_min_x: number | null;
  bbox_min_y: number | null;
  bbox_min_z: number | null;
  bbox_max_x: number | null;
  bbox_max_y: number | null;
  bbox_max_z: number | null;
  param_hash: string;
  params_json: string;
}

function toElementRow(row: SnapshotElementDbRow): SnapshotElementRow {
  return {
    elementId: row.element_id,
    uniqueId: row.unique_id,
    categoryKey: row.category_key,
    category: row.category,
    familyName: row.family_name,
    typeName: row.type_name,
    typeId: row.type_id,
    levelName: row.level_name,
    roomName: row.room_name,
    roomNumber: row.room_number,
    bboxMinX: row.bbox_min_x,
    bboxMinY: row.bbox_min_y,
    bboxMinZ: row.bbox_min_z,
    bboxMaxX: row.bbox_max_x,
    bboxMaxY: row.bbox_max_y,
    bboxMaxZ: row.bbox_max_z,
    paramHash: row.param_hash,
    paramsJson: row.params_json,
  };
}

/**
 * Every element of one snapshot — what `compare_model_versions` (REV-171) diffs.
 *
 * Read whole rather than paged: the diff is a join over both sides, and a
 * snapshot already sits in SQLite for exactly this kind of question. A few
 * hundred thousand rows of plain columns is well within what a Node process
 * holds twice over without trouble; paging would only move the same cost into
 * the caller.
 */
export function getSnapshotElements(db: Database, snapshotId: number): SnapshotElementRow[] {
  ensureSnapshotSchema(db);

  const rows = db
    .prepare(
      `SELECT unique_id, element_id, category_key, category, family_name, type_name, type_id,
              level_name, room_name, room_number,
              bbox_min_x, bbox_min_y, bbox_min_z, bbox_max_x, bbox_max_y, bbox_max_z,
              param_hash, params_json
         FROM snapshot_elements
        WHERE snapshot_id = ?`
    )
    .all(snapshotId) as SnapshotElementDbRow[];

  return rows.map(toElementRow);
}

/** The parameter key → display label map recorded when the snapshot was taken. */
export function getSnapshotParameterLabels(db: Database, snapshotId: number): Record<string, string> {
  ensureSnapshotSchema(db);

  const row = db
    .prepare("SELECT parameter_labels FROM model_snapshots WHERE id = ?")
    .get(snapshotId) as { parameter_labels: string | null } | undefined;

  if (!row?.parameter_labels) return {};

  try {
    const parsed = JSON.parse(row.parameter_labels);
    return parsed && typeof parsed === "object" ? (parsed as Record<string, string>) : {};
  } catch {
    return {};
  }
}

/** Elements per level — the grouping REV-171 will report the diff in. */
export function snapshotLevelBreakdown(
  db: Database,
  snapshotId: number,
  limit = 20
): SnapshotBreakdownRow[] {
  ensureSnapshotSchema(db);

  return db
    .prepare(
      `SELECT level_name AS key, COUNT(*) AS count
         FROM snapshot_elements
        WHERE snapshot_id = ? AND level_name <> ''
        GROUP BY level_name
        ORDER BY count DESC
        LIMIT ?`
    )
    .all(snapshotId, limit) as SnapshotBreakdownRow[];
}
