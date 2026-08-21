import { createHash } from "node:crypto";

/**
 * Turning what Revit hands back into what a snapshot stores (REV-170).
 *
 * All of it is pure. That is the point: the rule that decides whether two
 * versions of an element count as "the same" is the foundation the whole эпик
 * «что изменилось» stands on, and a rule that can only be exercised by opening a
 * 100 MB model in Revit is a rule nobody checks. Here it is exercised by
 * `modelSnapshot.test.ts`, and the plugin stays a reader that reports facts.
 */

/** A parameter value as the plugin sends it — Revit's own units, untouched. */
export type SnapshotParameterValue = string | number | boolean | null | undefined;

export interface SnapshotPoint {
  x: number;
  y: number;
  z: number;
}

export interface SnapshotBox {
  min: SnapshotPoint;
  max: SnapshotPoint;
}

/** One element as `export_model_snapshot` reports it. */
export interface RawSnapshotElement {
  elementId: number;
  uniqueId: string;
  categoryKey?: string;
  category?: string;
  familyName?: string;
  typeName?: string;
  typeId?: number;
  levelName?: string;
  roomName?: string;
  roomNumber?: string;
  boundingBoxMm?: SnapshotBox | null;
  parameters?: Record<string, SnapshotParameterValue>;
}

/** One row of `snapshot_elements`. */
export interface SnapshotElementRow {
  elementId: number;
  uniqueId: string;
  categoryKey: string;
  category: string;
  familyName: string;
  typeName: string;
  typeId: number | null;
  levelName: string;
  roomName: string;
  roomNumber: string;
  bboxMinX: number | null;
  bboxMinY: number | null;
  bboxMinZ: number | null;
  bboxMaxX: number | null;
  bboxMaxY: number | null;
  bboxMaxZ: number | null;
  paramHash: string;
  paramsJson: string;
}

/**
 * Decimal places kept when hashing a number.
 *
 * Values arrive in Revit's internal feet, so 1e-6 is a third of a micrometre —
 * far below anything anyone draws, and far above the float noise a regeneration
 * leaves behind. Without the rounding, a wall nobody touched comes back
 * 3.9999999999999996 instead of 4 and the snapshot reports it as edited.
 */
const VALUE_PRECISION = 6;

/**
 * Decimal places kept for a bounding box, in mm. A tenth of a millimetre is
 * already finer than Revit's own tolerance; keeping more would only record noise.
 */
const GEOMETRY_PRECISION = 1;

/** Hex characters kept from the SHA-1. 64 bits over ~300k rows: collisions are not the risk here. */
const HASH_LENGTH = 16;

function roundTo(value: number, digits: number): number {
  const factor = 10 ** digits;
  const rounded = Math.round(value * factor) / factor;
  // -0 and 0 are the same number but not the same string, and the hash is built
  // from strings.
  return Object.is(rounded, -0) ? 0 : rounded;
}

/**
 * One parameter value as a canonical string.
 *
 * Empty is empty: null, undefined, an empty string and a missing parameter all
 * normalise to "" and are then dropped from the hash together. They have to,
 * because the plugin already omits a parameter it read as null — if a blank
 * value hashed differently from an absent one, every element carrying an unfilled
 * parameter would flicker between two hashes for no reason.
 */
export function normalizeParameterValue(value: SnapshotParameterValue): string {
  if (value === null || value === undefined) return "";
  if (typeof value === "boolean") return value ? "1" : "0";

  if (typeof value === "number") {
    if (!Number.isFinite(value)) return "";
    return String(roundTo(value, VALUE_PRECISION));
  }

  // Trimmed, but not otherwise touched: «Ст-1» and «Ст - 1» are different marks,
  // and collapsing the difference would hide a real edit.
  return value.trim();
}

/**
 * The parameters as they are hashed and stored: normalised, blanks dropped, keys
 * in a fixed order.
 *
 * The order is by UTF-16 code unit rather than `localeCompare`, which sorts
 * differently depending on the machine's locale — two architects would then
 * produce two different hashes for the same unchanged element.
 */
export function canonicalParameters(
  parameters: Record<string, SnapshotParameterValue> | undefined
): Record<string, string> {
  if (!parameters) return {};

  const canonical: Record<string, string> = {};
  const keys = Object.keys(parameters).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));

  for (const key of keys) {
    const value = normalizeParameterValue(parameters[key]);
    if (value === "") continue;
    canonical[key] = value;
  }

  return canonical;
}

/**
 * The hash a diff compares elements on.
 *
 * Keyed on names and values only, so the order Revit happened to enumerate the
 * parameters in cannot change it — a snapshot taken today and one taken next week
 * must agree about an element nobody touched. Delimiters are control characters
 * that cannot occur in a Revit parameter name, so "a=1, b=2" and "a=1b, =2" do not
 * collide.
 */
export function hashParameters(
  parameters: Record<string, SnapshotParameterValue> | undefined
): string {
  const canonical = canonicalParameters(parameters);
  const parts: string[] = [];

  for (const [key, value] of Object.entries(canonical)) {
    parts.push(`${key}\u0000${value}`);
  }

  return createHash("sha1").update(parts.join("\u0001"), "utf8").digest("hex").slice(0, HASH_LENGTH);
}

function roundedCoordinate(value: unknown): number | null {
  if (typeof value !== "number" || !Number.isFinite(value)) return null;
  return roundTo(value, GEOMETRY_PRECISION);
}

/** A snapshot row from what the plugin reported. Missing fields become blanks, never `undefined`. */
export function toSnapshotRow(raw: RawSnapshotElement): SnapshotElementRow {
  const box = raw.boundingBoxMm ?? null;

  return {
    elementId: Number(raw.elementId) || 0,
    uniqueId: raw.uniqueId ?? "",
    categoryKey: raw.categoryKey ?? "",
    category: raw.category ?? "",
    familyName: raw.familyName ?? "",
    typeName: raw.typeName ?? "",
    typeId: typeof raw.typeId === "number" && raw.typeId !== 0 ? raw.typeId : null,
    levelName: raw.levelName ?? "",
    roomName: raw.roomName ?? "",
    roomNumber: raw.roomNumber ?? "",
    bboxMinX: roundedCoordinate(box?.min?.x),
    bboxMinY: roundedCoordinate(box?.min?.y),
    bboxMinZ: roundedCoordinate(box?.min?.z),
    bboxMaxX: roundedCoordinate(box?.max?.x),
    bboxMaxY: roundedCoordinate(box?.max?.y),
    bboxMaxZ: roundedCoordinate(box?.max?.z),
    paramHash: hashParameters(raw.parameters),
    paramsJson: JSON.stringify(canonicalParameters(raw.parameters)),
  };
}

/**
 * Rows for one page, with elements the plugin could not identify left out.
 *
 * A row with no `uniqueId` cannot be matched against anything in a later
 * snapshot, so storing it would only add a phantom "удалено" to the next diff.
 * Duplicates within a page are collapsed for the same reason the table is keyed
 * on the pair: one element, one row.
 */
export function toSnapshotRows(elements: RawSnapshotElement[]): SnapshotElementRow[] {
  const rows = new Map<string, SnapshotElementRow>();

  for (const element of elements) {
    if (!element || typeof element.uniqueId !== "string" || element.uniqueId === "") continue;
    rows.set(element.uniqueId, toSnapshotRow(element));
  }

  return [...rows.values()];
}

/**
 * The label a snapshot gets when the architect did not name it. Dotted date the
 * way a штамп spells it, plus the time, because two выдачи can land on one day.
 */
export function defaultSnapshotLabel(now: Date = new Date()): string {
  const pad = (value: number) => String(value).padStart(2, "0");
  return (
    `снимок ${pad(now.getDate())}.${pad(now.getMonth() + 1)}.${now.getFullYear()} ` +
    `${pad(now.getHours())}:${pad(now.getMinutes())}`
  );
}
