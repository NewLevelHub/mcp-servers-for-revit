import type { SnapshotElementRow } from "./modelSnapshot.js";

/**
 * Comparing two model snapshots — «что изменилось с прошлой выдачи» человеческим
 * языком (REV-171).
 *
 * Pure arithmetic over two arrays of `SnapshotElementRow`, same as
 * `modelSnapshot.ts` (REV-170): the rule that decides whether an element counts
 * as changed, moved, or just noisy is exercised in `modelDiff.test.ts` against
 * synthetic rows, not against a 100 MB model in Revit.
 *
 * ## Why a moved element is not "removed" + "added"
 *
 * Elements are matched by `uniqueId`, which Revit keeps for as long as the
 * element itself is not deleted and redrawn from scratch. A wall that was
 * dragged three metres keeps its UniqueId, so it lands in the "present in both"
 * bucket and is reported as one `moved` entry — the pairing is free, it comes
 * from how the snapshot is keyed, not from any geometry matching done here.
 *
 * ## Why some parameters never make it into the diff
 *
 * `HOST_AREA_COMPUTED`, `HOST_VOLUME_COMPUTED`, `CURVE_ELEM_LENGTH` and
 * `ROOM_PERIMETER` are Revit's own read-only, recomputed numbers — see
 * `SnapshotParameterSet.cs`. A floor two rooms away recomputes its area every
 * time an unrelated wall moves; if that counted as a change, every выдача would
 * report dozens of elements nobody touched. `ROOM_AREA` is deliberately kept:
 * it is the one computed number the whole эпик exists to report — «площадь
 * кв. 45 выросла на 4 м²» is a `ROOM_AREA` change, and it stays in the diff.
 */

// --- units --------------------------------------------------------------

/** Decimal feet → millimetres, Revit's own conversion factor. */
const FEET_TO_MM = 304.8;
/** Square feet → square metres. */
const SQFT_TO_SQM = 0.09290304;
/** Cubic feet → cubic metres. */
const CUFT_TO_CUM = 0.0283168;

type ParameterUnit = "length" | "area" | "volume" | "plain";

/**
 * Units for exactly the fixed key set `SnapshotParameterSet.cs` reads — the
 * plugin sends every one of them in Revit's own internal units (feet), and a
 * diff that showed "9.84 → 10.2" for a wall height would need the architect to
 * do the conversion Revit itself already knows. Anything outside this list
 * (an office's own shared parameters, passed through `extraParameters`) is
 * shown as Revit reported it — its unit is not knowable from a snapshot alone.
 */
const PARAMETER_UNITS: Record<string, ParameterUnit> = {
  INSTANCE_ELEVATION_PARAM: "length",
  INSTANCE_FREE_HOST_OFFSET_PARAM: "length",
  INSTANCE_SILL_HEIGHT_PARAM: "length",
  INSTANCE_HEAD_HEIGHT_PARAM: "length",
  FAMILY_BASE_LEVEL_OFFSET_PARAM: "length",
  FAMILY_TOP_LEVEL_OFFSET_PARAM: "length",
  WALL_BASE_OFFSET: "length",
  WALL_TOP_OFFSET: "length",
  WALL_USER_HEIGHT_PARAM: "length",
  FLOOR_HEIGHTABOVELEVEL_PARAM: "length",
  LEVEL_ELEV: "length",
  CURVE_ELEM_LENGTH: "length",
  ROOM_PERIMETER: "length",
  ROOM_HEIGHT: "length",
  ROOM_UPPER_OFFSET: "length",
  HOST_AREA_COMPUTED: "area",
  ROOM_AREA: "area",
  HOST_VOLUME_COMPUTED: "volume",
};

/** See the file header — recomputed, not edited. Excluded from the diff entirely. */
export const VOLATILE_PARAMETER_KEYS: ReadonlySet<string> = new Set([
  "CURVE_ELEM_LENGTH",
  "HOST_AREA_COMPUTED",
  "HOST_VOLUME_COMPUTED",
  "ROOM_PERIMETER",
]);

function round(value: number, digits: number): number {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}

/** A stored parameter value, converted into what an architect reads. */
export function formatParameterValue(key: string, raw: string | undefined): string | null {
  if (raw === undefined || raw === "") return null;

  const unit = PARAMETER_UNITS[key] ?? "plain";
  if (unit === "plain") return raw;

  const value = Number(raw);
  if (!Number.isFinite(value)) return raw;

  switch (unit) {
    case "length":
      return `${Math.round(value * FEET_TO_MM)} мм`;
    case "area":
      return `${round(value * SQFT_TO_SQM, 2)} м²`;
    case "volume":
      return `${round(value * CUFT_TO_CUM, 2)} м³`;
  }
}

// --- matching -------------------------------------------------------------

export type ChangeKind = "added" | "removed" | "modified";

export interface ParameterChange {
  key: string;
  label: string;
  /** Human-readable, converted to mm/m²/m³ where the unit is known. */
  oldValue: string | null;
  newValue: string | null;
  /** Exactly as stored — Revit's own units, for a caller that wants to compute with it. */
  oldRaw: string | null;
  newRaw: string | null;
}

export interface ElementChange {
  uniqueId: string;
  elementId: number;
  category: string;
  categoryKey: string;
  /** "Стена «Кладка 250»" — the best identity a snapshot row can give. */
  label: string;
  kind: ChangeKind;
  /** Current level for added/modified, last known level for removed. */
  level: string;
  /** Same idea as `level`, formatted — "" when the element has no room. */
  room: string;
  moved: boolean;
  moveDistanceMm: number | null;
  levelChanged: boolean;
  oldLevel?: string;
  roomChanged: boolean;
  oldRoom?: string;
  typeChanged: boolean;
  oldType?: string;
  newType?: string;
  changedParameters: ParameterChange[];
  /**
   * Bounding-box centre in mm, current position for added/modified, last known
   * position for removed — where REV-172 anchors a change for clustering into a
   * revision cloud. `null` when the snapshot carries no bounding box.
   */
  location: { x: number; y: number; z: number } | null;
}

export interface DiffOptions {
  /** Below this, a bounding-box shift is float noise, not a move. */
  moveToleranceMm?: number;
}

/** A grid drawn or dragged by hand never lands on the exact same coordinate. */
export const DEFAULT_MOVE_TOLERANCE_MM = 5;

function roomLabel(number: string | undefined, name: string | undefined): string {
  const num = (number ?? "").trim();
  const nm = (name ?? "").trim();
  if (!num && !nm) return "";
  if (num && nm) return `пом. ${num} «${nm}»`;
  return num ? `пом. ${num}` : `«${nm}»`;
}

function describeElement(row: SnapshotElementRow): string {
  const category = row.category || row.categoryKey || "Элемент";
  const type = row.typeName || row.familyName;
  return type ? `${category} «${type}»` : category;
}

function safeParseParams(json: string): Record<string, string> {
  try {
    const parsed = JSON.parse(json);
    return parsed && typeof parsed === "object" ? (parsed as Record<string, string>) : {};
  } catch {
    return {};
  }
}

function bboxCenter(row: SnapshotElementRow): { x: number; y: number; z: number } | null {
  if (
    row.bboxMinX == null ||
    row.bboxMinY == null ||
    row.bboxMinZ == null ||
    row.bboxMaxX == null ||
    row.bboxMaxY == null ||
    row.bboxMaxZ == null
  ) {
    return null;
  }

  return {
    x: (row.bboxMinX + row.bboxMaxX) / 2,
    y: (row.bboxMinY + row.bboxMaxY) / 2,
    z: (row.bboxMinZ + row.bboxMaxZ) / 2,
  };
}

function distanceMm(a: { x: number; y: number; z: number }, b: { x: number; y: number; z: number }): number {
  return Math.sqrt((a.x - b.x) ** 2 + (a.y - b.y) ** 2 + (a.z - b.z) ** 2);
}

function diffParameters(
  oldParams: Record<string, string>,
  newParams: Record<string, string>,
  labels: Record<string, string>
): ParameterChange[] {
  const keys = new Set([...Object.keys(oldParams), ...Object.keys(newParams)]);
  const changes: ParameterChange[] = [];

  for (const key of keys) {
    if (VOLATILE_PARAMETER_KEYS.has(key)) continue;

    const oldRaw = oldParams[key];
    const newRaw = newParams[key];
    if (oldRaw === newRaw) continue;

    changes.push({
      key,
      label: labels[key] ?? key,
      oldValue: formatParameterValue(key, oldRaw),
      newValue: formatParameterValue(key, newRaw),
      oldRaw: oldRaw ?? null,
      newRaw: newRaw ?? null,
    });
  }

  return changes.sort((a, b) => a.label.localeCompare(b.label));
}

function buildAdded(row: SnapshotElementRow): ElementChange {
  return {
    uniqueId: row.uniqueId,
    elementId: row.elementId,
    category: row.category || row.categoryKey,
    categoryKey: row.categoryKey,
    label: describeElement(row),
    kind: "added",
    level: row.levelName,
    room: roomLabel(row.roomNumber, row.roomName),
    moved: false,
    moveDistanceMm: null,
    levelChanged: false,
    roomChanged: false,
    typeChanged: false,
    changedParameters: [],
    location: bboxCenter(row),
  };
}

function buildRemoved(row: SnapshotElementRow): ElementChange {
  return {
    uniqueId: row.uniqueId,
    elementId: row.elementId,
    category: row.category || row.categoryKey,
    categoryKey: row.categoryKey,
    label: describeElement(row),
    kind: "removed",
    level: row.levelName,
    room: roomLabel(row.roomNumber, row.roomName),
    moved: false,
    moveDistanceMm: null,
    levelChanged: false,
    roomChanged: false,
    typeChanged: false,
    changedParameters: [],
    location: bboxCenter(row),
  };
}

/** `null` when the element did not change — the caller drops it from the diff. */
function buildModified(
  oldRow: SnapshotElementRow,
  newRow: SnapshotElementRow,
  parameterLabels: Record<string, string>,
  toleranceMm: number
): ElementChange | null {
  let moved = false;
  let moveDistanceMm: number | null = null;

  const oldCenter = bboxCenter(oldRow);
  const newCenter = bboxCenter(newRow);
  if (oldCenter && newCenter) {
    const distance = distanceMm(oldCenter, newCenter);
    if (distance > toleranceMm) {
      moved = true;
      moveDistanceMm = round(distance, 1);
    }
  }

  const levelChanged = oldRow.levelName !== newRow.levelName;
  const roomChanged = oldRow.roomName !== newRow.roomName || oldRow.roomNumber !== newRow.roomNumber;
  const typeChanged = oldRow.typeName !== newRow.typeName || oldRow.familyName !== newRow.familyName;

  const changedParameters = diffParameters(
    safeParseParams(oldRow.paramsJson),
    safeParseParams(newRow.paramsJson),
    parameterLabels
  );

  if (!moved && !levelChanged && !roomChanged && !typeChanged && changedParameters.length === 0) {
    return null;
  }

  return {
    uniqueId: newRow.uniqueId,
    elementId: newRow.elementId,
    category: newRow.category || newRow.categoryKey,
    categoryKey: newRow.categoryKey,
    label: describeElement(newRow),
    kind: "modified",
    level: newRow.levelName,
    room: roomLabel(newRow.roomNumber, newRow.roomName),
    moved,
    moveDistanceMm,
    levelChanged,
    oldLevel: levelChanged ? oldRow.levelName : undefined,
    roomChanged,
    oldRoom: roomChanged ? roomLabel(oldRow.roomNumber, oldRow.roomName) : undefined,
    typeChanged,
    oldType: typeChanged ? describeElement(oldRow) : undefined,
    newType: typeChanged ? describeElement(newRow) : undefined,
    changedParameters,
    location: newCenter ?? oldCenter,
  };
}

/**
 * The diff itself: everything added, removed, or changed between two
 * snapshots (or a snapshot and a fresh read of the model). Unchanged elements
 * are not in the result at all — that is what keeps a 300k-element model from
 * producing a 300k-line answer.
 */
export function diffSnapshotElements(
  oldRows: readonly SnapshotElementRow[],
  newRows: readonly SnapshotElementRow[],
  parameterLabels: Record<string, string> = {},
  options: DiffOptions = {}
): ElementChange[] {
  const toleranceMm = options.moveToleranceMm ?? DEFAULT_MOVE_TOLERANCE_MM;

  const oldByKey = new Map(oldRows.map((row) => [row.uniqueId, row]));
  const newByKey = new Map(newRows.map((row) => [row.uniqueId, row]));
  const changes: ElementChange[] = [];

  for (const [uniqueId, newRow] of newByKey) {
    const oldRow = oldByKey.get(uniqueId);
    if (!oldRow) {
      changes.push(buildAdded(newRow));
      continue;
    }

    const change = buildModified(oldRow, newRow, parameterLabels, toleranceMm);
    if (change) changes.push(change);
  }

  for (const [uniqueId, oldRow] of oldByKey) {
    if (!newByKey.has(uniqueId)) changes.push(buildRemoved(oldRow));
  }

  return changes;
}

// --- grouping ---------------------------------------------------------------

export interface RoomGroup {
  /** "" for elements outside any room. */
  room: string;
  changes: ElementChange[];
}

export interface LevelGroup {
  /** "" for elements with no level (rare — a level itself, or an unhosted annotation). */
  level: string;
  count: number;
  rooms: RoomGroup[];
}

/**
 * Changes grouped by level, then by room — the shape the acceptance criteria
 * asks for. Busiest level first, and within it busiest room first: that is
 * where the meeting's conversation starts.
 */
export function groupChanges(changes: readonly ElementChange[]): LevelGroup[] {
  const byLevel = new Map<string, Map<string, ElementChange[]>>();

  for (const change of changes) {
    let rooms = byLevel.get(change.level);
    if (!rooms) {
      rooms = new Map();
      byLevel.set(change.level, rooms);
    }

    let bucket = rooms.get(change.room);
    if (!bucket) {
      bucket = [];
      rooms.set(change.room, bucket);
    }
    bucket.push(change);
  }

  const levels: LevelGroup[] = [];
  for (const [level, rooms] of byLevel) {
    const roomGroups = [...rooms.entries()]
      .map(([room, list]) => ({ room, changes: list }))
      .sort((a, b) => b.changes.length - a.changes.length || a.room.localeCompare(b.room));

    const count = roomGroups.reduce((sum, group) => sum + group.changes.length, 0);
    levels.push({ level, count, rooms: roomGroups });
  }

  return levels.sort((a, b) => b.count - a.count || a.level.localeCompare(b.level));
}

// --- human text ---------------------------------------------------------------

/** One change, as one line an architect can read without opening Revit. */
export function describeChange(change: ElementChange): string {
  const idSuffix = ` (id ${change.elementId})`;

  if (change.kind === "added") {
    const where = change.room ? `, ${change.room}` : "";
    return `Добавлено: ${change.label}${idSuffix}${where}.`;
  }

  if (change.kind === "removed") {
    const where = change.room ? `, было ${change.room}` : "";
    return `Удалено: ${change.label}${idSuffix}${where}.`;
  }

  const details: string[] = [];
  if (change.moved && change.moveDistanceMm != null) {
    details.push(`смещение ${change.moveDistanceMm} мм`);
  }
  if (change.levelChanged) {
    details.push(`уровень: «${change.oldLevel || "—"}» → «${change.level || "—"}»`);
  }
  if (change.roomChanged) {
    details.push(`помещение: ${change.oldRoom || "—"} → ${change.room || "—"}`);
  }
  if (change.typeChanged) {
    details.push(`тип: ${change.oldType} → ${change.newType}`);
  }
  for (const parameter of change.changedParameters) {
    details.push(`${parameter.label}: ${parameter.oldValue ?? "—"} → ${parameter.newValue ?? "—"}`);
  }

  return `${change.label}${idSuffix}: ${details.join("; ")}.`;
}

export interface DiffCounts {
  added: number;
  removed: number;
  modified: number;
  moved: number;
  total: number;
}

export function countChanges(changes: readonly ElementChange[]): DiffCounts {
  let added = 0;
  let removed = 0;
  let modified = 0;
  let moved = 0;

  for (const change of changes) {
    if (change.kind === "added") added += 1;
    else if (change.kind === "removed") removed += 1;
    else {
      modified += 1;
      if (change.moved) moved += 1;
    }
  }

  return { added, removed, modified, moved, total: changes.length };
}

/** How many unmatched clusters get a line of their own before folding into "и ещё N". */
const CLUSTER_HEADLINE_LIMIT = 3;
/** A moved cluster smaller than this is not worth a clause in the headline. */
const MIN_CLUSTER_FOR_HEADLINE = 2;
/** Room area changes smaller than this are rounding, not a finding worth a sentence. */
const MIN_ROOM_AREA_DELTA_M2 = 0.5;

/**
 * The sentence a ГАП reads first: what moved where, and which помещения grew or
 * shrank. Built from the two things a meeting actually opens with — see the
 * ticket's own example, «переставлено 12 перегородок на 3 этаже, площадь
 * кв. 45 выросла на 4 м²».
 */
export function buildDiffHeadline(changes: readonly ElementChange[]): string {
  const counts = countChanges(changes);
  if (counts.total === 0) return "Изменений нет.";

  const clauses: string[] = [];

  // Moved elements, clustered by (level, category) — "12 стен переставлено на
  // 3 этаже" is one fact; twelve separate "стена переставлена" lines are not.
  const moveClusters = new Map<string, { level: string; category: string; count: number }>();
  for (const change of changes) {
    if (change.kind !== "modified" || !change.moved) continue;
    const key = `${change.level} ${change.category}`;
    const existing = moveClusters.get(key);
    if (existing) existing.count += 1;
    else moveClusters.set(key, { level: change.level, category: change.category, count: 1 });
  }

  const sortedClusters = [...moveClusters.values()]
    .filter((cluster) => cluster.count >= MIN_CLUSTER_FOR_HEADLINE)
    .sort((a, b) => b.count - a.count)
    .slice(0, CLUSTER_HEADLINE_LIMIT);

  for (const cluster of sortedClusters) {
    const where = cluster.level ? ` на уровне «${cluster.level}»` : "";
    clauses.push(`переставлено${where}: ${cluster.category} — ${cluster.count}`);
  }

  // The biggest room area swing — computed from the raw ROOM_AREA parameter,
  // not the formatted string, so the arithmetic is exact.
  let biggestRoom: { room: string; deltaM2: number } | null = null;
  for (const change of changes) {
    if (change.kind !== "modified" || change.categoryKey !== "OST_Rooms") continue;
    const area = change.changedParameters.find((p) => p.key === "ROOM_AREA");
    if (!area || area.oldRaw == null || area.newRaw == null) continue;

    const deltaM2 = (Number(area.newRaw) - Number(area.oldRaw)) * SQFT_TO_SQM;
    if (!Number.isFinite(deltaM2) || Math.abs(deltaM2) < MIN_ROOM_AREA_DELTA_M2) continue;
    if (!biggestRoom || Math.abs(deltaM2) > Math.abs(biggestRoom.deltaM2)) {
      biggestRoom = { room: change.room || change.label, deltaM2 };
    }
  }

  if (biggestRoom) {
    const verb = biggestRoom.deltaM2 > 0 ? "выросла" : "уменьшилась";
    clauses.push(`площадь ${biggestRoom.room} ${verb} на ${round(Math.abs(biggestRoom.deltaM2), 1)} м²`);
  }

  const summary = `Добавлено: ${counts.added}, удалено: ${counts.removed}, изменено: ${counts.modified}`;

  return clauses.length > 0 ? `${clauses.join(", ")} (${summary}).` : `${summary}.`;
}
