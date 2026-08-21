import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import Database from "better-sqlite3";
import { closeSnapshotDb, snapshotDb, snapshotDbPath } from "./snapshotDb.js";
import {
  beginSnapshot,
  deleteSnapshot,
  ensureSnapshotSchema,
  finishSnapshot,
  findSnapshotByLabel,
  insertSnapshotElements,
  listSnapshots,
  pruneSnapshots,
  snapshotCategoryBreakdown,
  snapshotLevelBreakdown,
} from "./snapshots.js";
import { toSnapshotRows, type RawSnapshotElement } from "../utils/modelSnapshot.js";

/**
 * The storage half of REV-170, against an in-memory database.
 *
 * What is pinned here is the promise that a re-run does not duplicate anything
 * and that the база не растёт бесконечно — both of which are invisible until the
 * disk fills or a diff reports a model twice its real size.
 */

function open() {
  const db = new Database(":memory:");
  db.pragma("foreign_keys = ON");
  ensureSnapshotSchema(db);
  return db;
}

function element(overrides: Partial<RawSnapshotElement> = {}): RawSnapshotElement {
  return {
    elementId: 1,
    uniqueId: "u-1",
    categoryKey: "OST_Walls",
    category: "Стены",
    levelName: "2 этаж",
    parameters: { ALL_MODEL_MARK: "Ст-1" },
    ...overrides,
  };
}

function countElements(db: Database.Database, snapshotId: number): number {
  const row = db
    .prepare("SELECT COUNT(*) AS n FROM snapshot_elements WHERE snapshot_id = ?")
    .get(snapshotId) as { n: number };
  return row.n;
}

test("the same page written twice leaves one row per element", () => {
  const db = open();
  const { id } = beginSnapshot(db, { modelName: "Короткий блок", label: "выдача АР" });
  const rows = toSnapshotRows([element(), element({ uniqueId: "u-2", elementId: 2 })]);

  insertSnapshotElements(db, id, rows);
  insertSnapshotElements(db, id, rows);

  assert.equal(countElements(db, id), 2);
});

test("re-running a снимок under the same label replaces it instead of piling up", () => {
  const db = open();

  const first = beginSnapshot(db, { modelName: "Короткий блок", label: "выдача АР 19.08.2026" });
  insertSnapshotElements(db, first.id, toSnapshotRows([element(), element({ uniqueId: "u-2" })]));
  finishSnapshot(db, first.id, { durationMs: 1000 });

  const second = beginSnapshot(db, { modelName: "Короткий блок", label: "выдача АР 19.08.2026" });
  insertSnapshotElements(db, second.id, toSnapshotRows([element()]));
  finishSnapshot(db, second.id, { durationMs: 900 });

  assert.equal(second.replaced, true);
  assert.equal(listSnapshots(db, "Короткий блок").length, 1);
  // And the elements of the replaced snapshot went with it — otherwise the row
  // count would still show the two of the first run.
  assert.equal(countElements(db, second.id), 1);
  assert.equal(countElements(db, first.id), 0);
});

test("the same label under a different model is a different snapshot", () => {
  const db = open();

  beginSnapshot(db, { modelName: "Корпус 1", label: "выдача АР" });
  beginSnapshot(db, { modelName: "Корпус 2", label: "выдача АР" });

  assert.equal(listSnapshots(db).length, 2);
  assert.equal(listSnapshots(db, "Корпус 1").length, 1);
});

test("a snapshot is building until it is finished", () => {
  const db = open();
  const { id } = beginSnapshot(db, { modelName: "Короткий блок", label: "выдача" });

  assert.equal(listSnapshots(db)[0].status, "building");

  insertSnapshotElements(db, id, toSnapshotRows([element()]));
  const stored = finishSnapshot(db, id, { durationMs: 1234 });

  const header = findSnapshotByLabel(db, "Короткий блок", "выдача");
  assert.equal(stored, 1);
  assert.equal(header?.status, "ready");
  assert.equal(header?.elementCount, 1);
  assert.equal(header?.durationMs, 1234);
});

test("an interrupted snapshot is stored as partial, not as ready", () => {
  const db = open();
  const { id } = beginSnapshot(db, { modelName: "Короткий блок", label: "оборвался" });

  insertSnapshotElements(db, id, toSnapshotRows([element()]));
  finishSnapshot(db, id, { durationMs: 10, status: "partial" });

  assert.equal(findSnapshotByLabel(db, "Короткий блок", "оборвался")?.status, "partial");
});

test("deleting a snapshot takes its elements with it", () => {
  const db = open();
  const { id } = beginSnapshot(db, { modelName: "Короткий блок", label: "выдача" });
  insertSnapshotElements(db, id, toSnapshotRows([element(), element({ uniqueId: "u-2" })]));

  assert.equal(deleteSnapshot(db, id), true);
  assert.equal(listSnapshots(db).length, 0);
  assert.equal(countElements(db, id), 0);
});

test("deleting a snapshot that is not there says so instead of throwing", () => {
  const db = open();

  assert.equal(deleteSnapshot(db, 4242), false);
});

test("pruning keeps the newest and drops the rest", () => {
  const db = open();

  for (let i = 1; i <= 4; i++) {
    const { id } = beginSnapshot(db, { modelName: "Короткий блок", label: `выдача ${i}` });
    insertSnapshotElements(db, id, toSnapshotRows([element()]));
    finishSnapshot(db, id, { durationMs: 1 });
    // beginSnapshot stamps Date.now(); without a nudge four snapshots taken in
    // the same millisecond have no order to keep.
    db.prepare("UPDATE model_snapshots SET taken_at = ? WHERE id = ?").run(i * 1000, id);
  }

  const pruned = pruneSnapshots(db, "Короткий блок", 2);

  assert.deepEqual(
    pruned.map((snapshot) => snapshot.label),
    ["выдача 2", "выдача 1"]
  );
  assert.deepEqual(
    listSnapshots(db, "Короткий блок").map((snapshot) => snapshot.label),
    ["выдача 4", "выдача 3"]
  );
});

test("pruning another model's snapshots is not this model's business", () => {
  const db = open();
  beginSnapshot(db, { modelName: "Корпус 1", label: "выдача" });
  beginSnapshot(db, { modelName: "Корпус 2", label: "выдача" });

  pruneSnapshots(db, "Корпус 1", 1);

  assert.equal(listSnapshots(db).length, 2);
});

test("the breakdown counts what was actually stored", () => {
  const db = open();
  const { id } = beginSnapshot(db, { modelName: "Короткий блок", label: "выдача" });

  insertSnapshotElements(
    db,
    id,
    toSnapshotRows([
      element({ uniqueId: "u-1" }),
      element({ uniqueId: "u-2" }),
      element({ uniqueId: "u-3" }),
      element({ uniqueId: "u-4", categoryKey: "OST_Doors", category: "Двери", levelName: "1 этаж" }),
      element({ uniqueId: "u-5", categoryKey: "OST_Doors", category: "Двери", levelName: "" }),
    ])
  );

  assert.deepEqual(snapshotCategoryBreakdown(db, id), [
    { key: "Стены", count: 3 },
    { key: "Двери", count: 2 },
  ]);
  // The element with no level is left out rather than counted under a blank one.
  assert.deepEqual(snapshotLevelBreakdown(db, id), [
    { key: "2 этаж", count: 3 },
    { key: "1 этаж", count: 1 },
  ]);
});

// --- where the file lives (REV-170) -----------------------------------------

/**
 * Snapshots must not sit under the add-in. `deploy-local` replaces
 * `revit-data.db` outright and the updater stages a whole new `mcp-server`
 * folder, so anything kept there is gone at the next update — and a comparison
 * with nothing to compare against is the one failure this эпик cannot afford.
 */
test("snapshots live outside the add-in, in the user profile", () => {
  const previous = process.env.REVIT_MCP_SNAPSHOT_DB;
  delete process.env.REVIT_MCP_SNAPSHOT_DB;

  try {
    const file = snapshotDbPath();

    assert.equal(path.dirname(file), path.join(os.homedir(), ".mcp-servers-for-revit"));
    assert.equal(path.basename(file), "model-snapshots.db");
    // The norm library's own file must not be the target.
    assert.doesNotMatch(file, /revit-data\.db$/);
    assert.doesNotMatch(file, /[\/]mcp-server[\/]/);
  } finally {
    if (previous === undefined) delete process.env.REVIT_MCP_SNAPSHOT_DB;
    else process.env.REVIT_MCP_SNAPSHOT_DB = previous;
  }
});

test("the snapshot database opens with its schema ready", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "snapshot-db-"));
  const file = path.join(dir, "model-snapshots.db");
  const previous = process.env.REVIT_MCP_SNAPSHOT_DB;
  process.env.REVIT_MCP_SNAPSHOT_DB = file;
  closeSnapshotDb();

  try {
    const db = snapshotDb();
    const { id } = beginSnapshot(db, { modelName: "Короткий блок", label: "выдача" });
    insertSnapshotElements(db, id, toSnapshotRows([element()]));

    assert.equal(finishSnapshot(db, id, { durationMs: 1 }), 1);
    assert.equal(fs.existsSync(file), true);
  } finally {
    closeSnapshotDb();
    if (previous === undefined) delete process.env.REVIT_MCP_SNAPSHOT_DB;
    else process.env.REVIT_MCP_SNAPSHOT_DB = previous;
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
