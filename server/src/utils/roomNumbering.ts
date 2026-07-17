/**
 * Rule-based room / apartment numbering (Autodesk 2027-style, localized).
 *
 * Pure logic: deterministic numbering plans over spatial units (rooms, or room
 * groups = apartments). Schemes: level prefix (101, 102… / 201, 202…) or
 * continuous through levels; traversal: snake rows or (counter)clockwise around
 * the group centroid; optional section prefixes with separators. Deterministic
 * geometry ordering makes re-numbering idempotent: same rooms + same rules →
 * same numbers; adding/removing a room shifts the sequence predictably.
 */

/** One numbering unit: a room, or an apartment (several room ids). */
export interface NumberingUnit {
  /** Element ids that receive the value (1 for a room, n for an apartment). */
  ids: number[];
  /** Display label for the preview (room name / apartment group value). */
  label: string;
  level: string;
  /** Plan position in mm (room centroid / mean of group centroids). */
  x: number;
  y: number;
  /** Current value of the target parameter (for diffing / idempotency). */
  current: string;
  /** Section / block value when section grouping is used. */
  section?: string;
}

export type NumberingScheme = "levelPrefix" | "continuous";
export type TraversalDirection = "snake" | "clockwise" | "counterclockwise";

export interface NumberingOptions {
  /** levelPrefix: номер = этаж×множитель + порядковый (101, 102…). continuous: сквозная. */
  scheme?: NumberingScheme;
  direction?: TraversalDirection;
  /** Sequence start within each level/section group (default 1). */
  startAt?: number;
  /** Level multiplier for levelPrefix scheme: 100 → 101…, 1000 → 1001… */
  levelMultiplier?: number;
  /** Prepend the section value: «А-101». */
  useSectionPrefix?: boolean;
  separator?: string;
  /** Zero-pad the sequence part: 2 → «01». Applies to the seq, not the level base. */
  padWidth?: number;
  /** Snake rows: rooms within this Y distance (mm) belong to one row. */
  rowToleranceMm?: number;
  /** Explicit level → index map for level names without digits. */
  levelIndexOverrides?: Record<string, number>;
}

export interface NumberingAssignment {
  ids: number[];
  label: string;
  level: string;
  section?: string;
  from: string;
  to: string;
}

export interface NumberingPlan {
  /** Units whose value changes, in application order. */
  assignments: NumberingAssignment[];
  /** Units already carrying the target number (idempotent re-run). */
  unchangedCount: number;
  totalUnits: number;
  warnings: string[];
}

/** «1 этаж» → 1, «Этаж 02» → 2, «L3» → 3; null when the name has no digits. */
export function parseLevelIndex(levelName: string): number | null {
  const match = (levelName ?? "").match(/-?\d+/);
  if (!match) return null;
  const value = Number(match[0]);
  return Number.isFinite(value) ? value : null;
}

/**
 * Resolve an index for every level: parsed digits first, otherwise ordinal of
 * the level among unparsed names (stable, warned about).
 */
export function resolveLevelIndexes(
  levelNames: string[],
  overrides: Record<string, number> = {}
): { indexes: Map<string, number>; warnings: string[] } {
  const warnings: string[] = [];
  const indexes = new Map<string, number>();
  const unparsed: string[] = [];

  for (const name of levelNames) {
    if (overrides[name] != null) {
      indexes.set(name, overrides[name]);
    } else {
      const parsed = parseLevelIndex(name);
      if (parsed != null) indexes.set(name, parsed);
      else unparsed.push(name);
    }
  }

  if (unparsed.length > 0) {
    const used = new Set(indexes.values());
    let next = 1;
    for (const name of [...unparsed].sort()) {
      while (used.has(next)) next++;
      indexes.set(name, next);
      used.add(next);
      warnings.push(
        `Уровень «${name}» без числа в имени — назначен индекс ${next}; задайте levelIndexOverrides для точности.`
      );
      next++;
    }
  }

  return { indexes, warnings };
}

/** Snake: rows top-to-bottom, direction alternates per row (змейка). */
function orderSnake(units: NumberingUnit[], rowToleranceMm: number): NumberingUnit[] {
  const sorted = [...units].sort((a, b) => b.y - a.y);
  const rows: NumberingUnit[][] = [];
  for (const unit of sorted) {
    const row = rows[rows.length - 1];
    if (row && Math.abs(row[0].y - unit.y) <= rowToleranceMm) row.push(unit);
    else rows.push([unit]);
  }

  const ordered: NumberingUnit[] = [];
  rows.forEach((row, index) => {
    row.sort((a, b) => a.x - b.x);
    if (index % 2 === 1) row.reverse();
    ordered.push(...row);
  });
  return ordered;
}

/** Angular order around the group centroid, starting at 12 o'clock. */
function orderAngular(units: NumberingUnit[], clockwise: boolean): NumberingUnit[] {
  const cx = units.reduce((sum, unit) => sum + unit.x, 0) / units.length;
  const cy = units.reduce((sum, unit) => sum + unit.y, 0) / units.length;

  return [...units].sort((a, b) => {
    // Angle from 12 o'clock; clockwise increases to the right.
    const angleOf = (unit: NumberingUnit) => {
      const raw = Math.atan2(unit.x - cx, unit.y - cy); // 0 at north, CW positive
      const angle = raw < 0 ? raw + 2 * Math.PI : raw;
      return clockwise ? angle : 2 * Math.PI - angle;
    };
    const diff = angleOf(a) - angleOf(b);
    if (Math.abs(diff) > 1e-9) return diff;
    // Deterministic tie-break for collinear rooms.
    return a.x - b.x || a.y - b.y;
  });
}

export function orderUnits(
  units: NumberingUnit[],
  direction: TraversalDirection,
  rowToleranceMm: number
): NumberingUnit[] {
  if (units.length <= 1) return [...units];
  switch (direction) {
    case "clockwise":
      return orderAngular(units, true);
    case "counterclockwise":
      return orderAngular(units, false);
    default:
      return orderSnake(units, rowToleranceMm);
  }
}

function formatNumber(
  seq: number,
  levelBase: number | null,
  section: string | undefined,
  options: Required<Pick<NumberingOptions, "useSectionPrefix" | "separator" | "padWidth">>
): string {
  const padded =
    options.padWidth > 0 ? String(seq).padStart(options.padWidth, "0") : String(seq);
  const numeric = levelBase != null ? String(levelBase + seq) : padded;
  const core = levelBase != null ? numeric : padded;
  return options.useSectionPrefix && section
    ? `${section}${options.separator}${core}`
    : core;
}

/**
 * Build a deterministic numbering plan. Grouping: section → level; ordering
 * within a group is geometric (direction); numbering per scheme. Only changed
 * units land in assignments — re-running the same plan is a no-op.
 */
export function buildNumberingPlan(
  units: NumberingUnit[],
  options: NumberingOptions = {}
): NumberingPlan {
  const scheme = options.scheme ?? "levelPrefix";
  const direction = options.direction ?? "snake";
  const startAt = options.startAt ?? 1;
  const levelMultiplier = options.levelMultiplier ?? 100;
  const useSectionPrefix = options.useSectionPrefix ?? true;
  const separator = options.separator ?? "-";
  const padWidth = options.padWidth ?? 0;
  const rowToleranceMm = options.rowToleranceMm ?? 3000;

  const warnings: string[] = [];
  const formatOptions = { useSectionPrefix, separator, padWidth };

  const { indexes: levelIndexes, warnings: levelWarnings } = resolveLevelIndexes(
    [...new Set(units.map((unit) => unit.level))],
    options.levelIndexOverrides ?? {}
  );
  warnings.push(...levelWarnings);

  // section → level → units
  const sections = new Map<string, Map<string, NumberingUnit[]>>();
  for (const unit of units) {
    const sectionKey = unit.section ?? "";
    const levels = sections.get(sectionKey) ?? new Map<string, NumberingUnit[]>();
    const list = levels.get(unit.level) ?? [];
    list.push(unit);
    levels.set(unit.level, list);
    sections.set(sectionKey, levels);
  }

  const assignments: NumberingAssignment[] = [];
  let unchangedCount = 0;

  for (const sectionKey of [...sections.keys()].sort()) {
    const levels = sections.get(sectionKey)!;
    const orderedLevels = [...levels.keys()].sort(
      (a, b) => (levelIndexes.get(a) ?? 0) - (levelIndexes.get(b) ?? 0)
    );

    let continuousSeq = startAt;

    for (const levelName of orderedLevels) {
      const ordered = orderUnits(levels.get(levelName)!, direction, rowToleranceMm);
      const levelIndex = levelIndexes.get(levelName) ?? 0;

      ordered.forEach((unit, position) => {
        let target: string;
        if (scheme === "continuous") {
          target = formatNumber(continuousSeq++, null, unit.section, formatOptions);
        } else {
          const base = levelIndex * levelMultiplier;
          target = formatNumber(startAt + position, base, unit.section, formatOptions);
        }

        if (unit.current === target) {
          unchangedCount++;
          return;
        }

        assignments.push({
          ids: unit.ids,
          label: unit.label,
          level: unit.level,
          section: unit.section,
          from: unit.current,
          to: target,
        });
      });
    }
  }

  return {
    assignments,
    unchangedCount,
    totalUnits: units.length,
    warnings,
  };
}
