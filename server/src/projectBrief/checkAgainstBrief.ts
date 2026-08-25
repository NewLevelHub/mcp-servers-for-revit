import type { BriefSourceRef } from "./types.js";

/**
 * Pure comparison logic for check_against_brief (REV-182): model room counts
 * and areas, matched against room_count/room_area_min requirements already
 * extracted and saved via query_project_brief. Kept Revit-free and pure so it
 * is unit-testable without a live connection — the Revit-facing tool file only
 * reads export_room_data and calls into this.
 *
 * Known limitation, stated plainly rather than silently guessed around: a
 * Revit Room element is one space (a kitchen, a bedroom), not one apartment.
 * Matching by room name works for single-space unit types named directly
 * ("Студия", "Офис 12", "Кладовая 3") but CANNOT find "2-комнатная квартира"
 * as a room name, because no single Room carries that name — an apartment is
 * several rooms grouped by a unit/apartment parameter this module does not
 * read. Every result below carries `matched:false` with an explanatory hint
 * rather than reporting a silent, wrong 0.
 */

export interface ModelRoom {
  name: string;
  area?: number;
  id?: number;
  number?: string;
}

export interface RoomGroup {
  normalizedName: string;
  /** First room's own name, for display — normalization lowercases/collapses whitespace. */
  sampleName: string;
  rooms: ModelRoom[];
}

function normalize(value: string): string {
  return value.toLowerCase().replace(/ё/g, "е").replace(/\s+/g, " ").trim();
}

export function groupRoomsByName(rooms: readonly ModelRoom[]): RoomGroup[] {
  const groups = new Map<string, RoomGroup>();
  for (const room of rooms) {
    const normalizedName = normalize(room.name ?? "");
    if (!normalizedName) continue;

    let group = groups.get(normalizedName);
    if (!group) {
      group = { normalizedName, sampleName: room.name, rooms: [] };
      groups.set(normalizedName, group);
    }
    group.rooms.push(room);
  }
  return [...groups.values()];
}

/** A room name "matches" an object when it equals it, or starts with it followed by a separator (e.g. "Студия 205" ~ "студия"). */
function roomGroupMatchesObject(group: RoomGroup, normalizedObject: string): boolean {
  return (
    group.normalizedName === normalizedObject ||
    group.normalizedName.startsWith(`${normalizedObject} `) ||
    group.normalizedName.startsWith(`${normalizedObject}-`)
  );
}

function findMatchingGroups(groups: readonly RoomGroup[], object: string): RoomGroup[] {
  const normalizedObject = normalize(object);
  return groups.filter((g) => roomGroupMatchesObject(g, normalizedObject));
}

export interface CountCheckResult {
  object: string;
  required: number;
  actual: number;
  matched: boolean;
  ok: boolean;
  message: string;
  source: BriefSourceRef;
}

export function checkRoomCount(
  object: string,
  required: number,
  source: BriefSourceRef,
  groups: readonly RoomGroup[]
): CountCheckResult {
  const matches = findMatchingGroups(groups, object);
  if (matches.length === 0) {
    return {
      object,
      required,
      actual: 0,
      matched: false,
      ok: false,
      message:
        `«${object}»: в модели не найдено помещений с таким названием — сверка не выполнена, ` +
        "а не «0 из требуемых». Возможно, это тип квартиры из нескольких Room, а не название одного помещения.",
      source,
    };
  }

  const actual = matches.reduce((sum, g) => sum + g.rooms.length, 0);
  return {
    object,
    required,
    actual,
    matched: true,
    ok: actual >= required,
    message:
      actual === required
        ? `«${object}»: требуется ${required} — в модели ${actual}. Совпадает.`
        : `«${object}»: требуется ${required} — в модели ${actual}.`,
    source,
  };
}

export interface AreaCheckResult {
  object: string;
  requiredMinM2: number;
  matched: boolean;
  checkedCount: number;
  violatingCount: number;
  violations: { name: string; area: number }[];
  ok: boolean;
  message: string;
  source: BriefSourceRef;
}

export function checkRoomAreaMin(
  object: string,
  requiredMinM2: number,
  source: BriefSourceRef,
  groups: readonly RoomGroup[]
): AreaCheckResult {
  const matches = findMatchingGroups(groups, object);
  if (matches.length === 0) {
    return {
      object,
      requiredMinM2,
      matched: false,
      checkedCount: 0,
      violatingCount: 0,
      violations: [],
      ok: false,
      message: `«${object}»: в модели не найдено помещений с таким названием — сверка площади не выполнена.`,
      source,
    };
  }

  const rooms = matches.flatMap((g) => g.rooms);
  const violations = rooms
    .filter((r) => r.area != null && r.area < requiredMinM2)
    .map((r) => ({ name: r.name, area: r.area as number }));

  return {
    object,
    requiredMinM2,
    matched: true,
    checkedCount: rooms.length,
    violatingCount: violations.length,
    violations,
    ok: violations.length === 0,
    message:
      violations.length === 0
        ? `«${object}»: площадь всех ${rooms.length} помещений не меньше ${requiredMinM2} м².`
        : `«${object}»: ${violations.length} из ${rooms.length} помещений меньше требуемых ${requiredMinM2} м².`,
    source,
  };
}
