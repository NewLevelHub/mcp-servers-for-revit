/**
 * Resolve living-room clear height for norm checks.
 *
 * Room.UnboundedHeight is often wrong in real projects (Revit default Limit Offset
 * = 8'-0" = 2438.4 mm with Upper Limit = same level). Prefer:
 *   clear ≈ ΔZ(level → next above-ground level) − floor slab on the upper level
 * When slab thickness is unknown, use storey ΔZ (optimistic upper bound of clear height).
 */

export interface LevelElevation {
  levelName: string;
  elevationMm: number;
  storeyKind?: string;
}

export type RoomHeightSource =
  | "level_clear"
  | "level_storey"
  | "room_unbounded"
  | "missing";

export interface ResolveRoomClearHeightInput {
  levelName?: string | null;
  /** Room.UnboundedHeight from Revit (mm). */
  unboundedHeightMm?: number | null;
  /**
   * Optional clear height already computed by the Revit exporter
   * (storey ΔZ − floor thickness). Preferred when > 0.
   */
  exportedClearHeightMm?: number | null;
  /** Optional storey ΔZ already computed by the exporter. */
  exportedStoreyHeightMm?: number | null;
  /** Optional median floor thickness on the upper level (mm). */
  floorThicknessMm?: number | null;
}

export interface ResolvedRoomClearHeight {
  heightMm: number | null;
  storeyHeightMm: number | null;
  source: RoomHeightSource;
  reason: string;
}

/** Classic Revit default room limit offsets (imperial). */
const IMPERIAL_DEFAULT_HEIGHTS_MM = [
  2438.4, // 8'-0"
  2743.2, // 9'-0"
  3048.0, // 10'-0"
  3657.6, // 12'-0"
];

const DEFAULT_MATCH_TOLERANCE_MM = 2;
/** Typical RC floor slab when thickness cannot be read from the model. */
export const DEFAULT_FLOOR_THICKNESS_MM = 300;

export function isLikelyDefaultRoomHeight(
  heightMm: number | null | undefined,
  toleranceMm = DEFAULT_MATCH_TOLERANCE_MM
): boolean {
  if (heightMm == null || !Number.isFinite(heightMm) || heightMm <= 0) {
    return false;
  }
  return IMPERIAL_DEFAULT_HEIGHTS_MM.some(
    (def) => Math.abs(heightMm - def) <= toleranceMm
  );
}

export function findStoreyHeightMmForLevel(
  levelName: string | null | undefined,
  levels: LevelElevation[]
): { storeyHeightMm: number; upperLevel: string } | null {
  if (!levelName || levels.length === 0) return null;

  const sorted = [...levels].sort((a, b) => a.elevationMm - b.elevationMm);
  const idx = sorted.findIndex(
    (level) => level.levelName.toLowerCase() === levelName.toLowerCase()
  );
  if (idx < 0 || idx >= sorted.length - 1) return null;

  const lower = sorted[idx];
  // Prefer next above-ground level when kinds are present; else next level by Z.
  let upper = sorted[idx + 1];
  if (lower.storeyKind === "aboveGround") {
    const nextAbove = sorted
      .slice(idx + 1)
      .find((level) => level.storeyKind === "aboveGround");
    if (nextAbove) upper = nextAbove;
  }

  const storeyHeightMm = upper.elevationMm - lower.elevationMm;
  if (!(storeyHeightMm > 0)) return null;
  return { storeyHeightMm, upperLevel: upper.levelName };
}

/**
 * Pick the height used for «высота помещений от пола до низа потолков».
 *
 * Priority:
 * 1) ΔZ from level list (export_tep_data) − floor thickness (export or 300 mm default)
 * 2) exported clearHeight when levels are unavailable
 * 3) Room UnboundedHeight only if it is not an imperial default and levels are missing
 */
export function resolveRoomClearHeight(
  input: ResolveRoomClearHeightInput,
  levels: LevelElevation[] = []
): ResolvedRoomClearHeight {
  const fromLevels = findStoreyHeightMmForLevel(input.levelName, levels);
  const storeyFromExport = positive(input.exportedStoreyHeightMm);
  const storeyHeightMm = fromLevels?.storeyHeightMm ?? storeyFromExport ?? null;

  const floorThickness =
    positive(input.floorThicknessMm) ??
    (storeyHeightMm != null ? DEFAULT_FLOOR_THICKNESS_MM : null);

  if (storeyHeightMm != null && floorThickness != null) {
    const clearFromLevels = Math.max(0, storeyHeightMm - floorThickness);
    const roomH = positive(input.unboundedHeightMm);
    const exportedClear = positive(input.exportedClearHeightMm);

    // Prefer exporter clear height when it matches level-based estimate (±150 mm).
    if (
      exportedClear != null &&
      Math.abs(exportedClear - clearFromLevels) <= 150
    ) {
      return {
        heightMm: round1(exportedClear),
        storeyHeightMm: round1(storeyHeightMm),
        source: "level_clear",
        reason:
          "clearHeight из export_room_data, согласован с ΔZ уровней",
      };
    }

    // Trust room UnboundedHeight only when consistent with storey − slab
    // and not an imperial default Limit Offset.
    if (
      roomH != null &&
      !isLikelyDefaultRoomHeight(roomH) &&
      roomH <= storeyHeightMm + 50 &&
      roomH >= clearFromLevels - 150 &&
      roomH <= clearFromLevels + 150
    ) {
      return {
        heightMm: round1(roomH),
        storeyHeightMm: round1(storeyHeightMm),
        source: "room_unbounded",
        reason:
          "UnboundedHeight комнаты согласован с ΔZ уровней (помещение настроено корректно)",
      };
    }

    return {
      heightMm: round1(clearFromLevels),
      storeyHeightMm: round1(storeyHeightMm),
      source: "level_clear",
      reason:
        roomH != null && isLikelyDefaultRoomHeight(roomH)
          ? `игнорирован дефолт Room Limit Offset ${round1(roomH)} мм; ` +
            `высота = ΔZ(${input.levelName}→${fromLevels?.upperLevel ?? "…"}) ${round1(storeyHeightMm)} − перекрытие ${round1(floorThickness)}`
          : `высота = ΔZ уровней ${round1(storeyHeightMm)} − перекрытие ${round1(floorThickness)} мм`,
    };
  }

  const exportedClear = positive(input.exportedClearHeightMm);
  if (exportedClear != null) {
    return {
      heightMm: round1(exportedClear),
      storeyHeightMm: storeyFromExport,
      source: "level_clear",
      reason: "clearHeight из export_room_data (уровни TEP недоступны)",
    };
  }

  if (storeyHeightMm != null) {
    return {
      heightMm: round1(storeyHeightMm),
      storeyHeightMm: round1(storeyHeightMm),
      source: "level_storey",
      reason: `высота этажа ΔZ уровней ${round1(storeyHeightMm)} мм (толщина перекрытия неизвестна)`,
    };
  }

  const roomH = positive(input.unboundedHeightMm);
  if (roomH != null && !isLikelyDefaultRoomHeight(roomH)) {
    return {
      heightMm: round1(roomH),
      storeyHeightMm: null,
      source: "room_unbounded",
      reason: "fallback: UnboundedHeight (уровни недоступны)",
    };
  }

  return {
    heightMm: null,
    storeyHeightMm: null,
    source: "missing",
    reason:
      roomH != null && isLikelyDefaultRoomHeight(roomH)
        ? `отброшен дефолт Room ${round1(roomH)} мм, уровней для ΔZ нет`
        : "нет ни уровней, ни надёжной высоты помещения",
  };
}

function positive(value: number | null | undefined): number | null {
  if (value == null || !Number.isFinite(value) || value <= 0) return null;
  return value;
}

function round1(value: number): number {
  return Math.round(value * 10) / 10;
}
