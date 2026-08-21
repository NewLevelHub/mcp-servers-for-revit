import Database from "better-sqlite3";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { ensureSnapshotSchema } from "./snapshots.js";

/**
 * Where model snapshots live (REV-170) — deliberately **not** in
 * `server/revit-data.db`, and deliberately not anywhere under the add-in.
 *
 * ## Why the ticket's file could not be the answer
 *
 * The ticket put snapshots in `revit-data.db` because SQLite is already wired up
 * there, and that half of the decision stands: hundreds of thousands of rows and
 * "what does this snapshot have that the previous one does not" is database work,
 * not a file in a profile.
 *
 * What the ticket could not have known is what that particular file is for.
 * `revit-data.db` is the **seed of the norm library**: it is built in the repo and
 * shipped, and both `deploy-local` (`Deploy-NormLibrary`) and the auto-updater
 * (`Update-RevitMcp.ps1`) replace it wholesale, keeping the old one only as
 * `revit-data.db.bak`. Snapshots written there would survive exactly until the
 * next update — and «что изменилось с прошлой выдачи» would then have nothing to
 * compare against, which is the one thing the whole эпик exists to do.
 *
 * A sibling file under `mcp-server/` does not help either: the updater stages a
 * complete new tree and swaps it in, carrying over only `Logs` and the settings.
 * Everything else in that folder is replaced by construction.
 *
 * ## So: the user profile
 *
 * `~/.mcp-servers-for-revit/` is outside the add-in entirely. The ribbon catalog
 * (REV-151) and the command metrics logs already live there for the same reason,
 * and nothing in the release path touches it. A snapshot taken before an update
 * is still there after it — and after a reinstall, and after a Revit version
 * upgrade.
 *
 * The норм library keeps its own file, unchanged and still freely replaceable.
 * The two things have opposite lifetimes: one ships with the build, the other
 * belongs to the architect.
 */

/** Overridable so tests never touch the real file. */
const PATH_ENV = "REVIT_MCP_SNAPSHOT_DB";

let instance: Database.Database | null = null;

export function snapshotDbPath(): string {
  const override = process.env[PATH_ENV];
  if (override && override.trim()) return override.trim();

  return path.join(os.homedir(), ".mcp-servers-for-revit", "model-snapshots.db");
}

/**
 * The snapshot database, opened on first use.
 *
 * Lazy on purpose: `db.ts` is imported by every tool module at registration
 * time, and creating a file in the user's profile merely because the server
 * started — for a session that may never take a snapshot — is not something a
 * tool registry should do.
 */
export function snapshotDb(): Database.Database {
  if (instance) return instance;

  const file = snapshotDbPath();
  fs.mkdirSync(path.dirname(file), { recursive: true });

  const database = new Database(file);
  database.pragma("foreign_keys = ON");
  // A snapshot is one long run of batched writes; WAL keeps that from blocking
  // a reader, and survives an interrupted run better than the rollback journal.
  database.pragma("journal_mode = WAL");
  ensureSnapshotSchema(database);

  instance = database;
  return instance;
}

/** Closes the handle, for tests and shutdown. */
export function closeSnapshotDb(): void {
  if (!instance) return;
  instance.close();
  instance = null;
}
