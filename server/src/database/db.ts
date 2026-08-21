import Database from "better-sqlite3";
import { join, dirname } from "path";
import { fileURLToPath } from "url";
import { ensureNormRulesSchema } from "../normatives/rulesStore.js";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

/** Database file next to server/ (revit-data.db). */
const DB_PATH = join(__dirname, "..", "..", "revit-data.db");

function openDatabase(): Database.Database {
  try {
    const database = new Database(DB_PATH);
    database.pragma("foreign_keys = ON");
    return database;
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new Error(
      `Failed to open SQLite (better-sqlite3) at ${DB_PATH}. ` +
        `Cursor often runs MCP with its bundled Node (ABI 127 / Node 22) while ` +
        `better-sqlite3 may be built for another ABI. Fix: set mcp.json ` +
        `"command" to your system Node (e.g. C:/Program Files/nodejs/node.exe) ` +
        `that matches the build, then reload MCP. Underlying error: ${detail}`
    );
  }
}

export const db = openDatabase();

export function initializeDatabase() {
  db.exec(`
    CREATE TABLE IF NOT EXISTS projects (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      project_name TEXT NOT NULL,
      project_path TEXT,
      project_number TEXT,
      project_address TEXT,
      client_name TEXT,
      project_status TEXT,
      author TEXT,
      timestamp INTEGER NOT NULL,
      last_updated INTEGER NOT NULL,
      metadata TEXT
    )
  `);

  db.exec(`
    CREATE TABLE IF NOT EXISTS rooms (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      project_id INTEGER NOT NULL,
      room_id TEXT NOT NULL,
      room_name TEXT,
      room_number TEXT,
      department TEXT,
      level TEXT,
      area REAL,
      perimeter REAL,
      occupancy TEXT,
      comments TEXT,
      timestamp INTEGER NOT NULL,
      metadata TEXT,
      FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
      UNIQUE(project_id, room_id)
    )
  `);

  db.exec(`
    CREATE INDEX IF NOT EXISTS idx_projects_name ON projects(project_name);
    CREATE INDEX IF NOT EXISTS idx_projects_timestamp ON projects(timestamp);
    CREATE INDEX IF NOT EXISTS idx_rooms_project_id ON rooms(project_id);
    CREATE INDEX IF NOT EXISTS idx_rooms_room_number ON rooms(room_number);
  `);

  ensureNormRulesSchema(db);
  // Model snapshots (REV-170) are NOT here. This file is the shipped seed of the
  // norm library and every update replaces it; snapshots have to outlive that,
  // so they live in the user profile — see `database/snapshotDb.ts`.
}

initializeDatabase();

export default db;
