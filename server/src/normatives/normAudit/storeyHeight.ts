/**
 * Storey height (ΔZ between levels) classification for REV-57.
 */

export interface LevelInput {
  levelName: string;
  elevationMm: number;
  storeyKind: string;
}

export type StoreyHeightStatus = "violation" | "nearLimit" | "compliant";

export interface ClassifiedStoreyHeight {
  /** Synthetic id based on lower level name hash — not a Revit element. */
  id: number;
  name: string;
  lowerLevel: string;
  upperLevel: string;
  status: StoreyHeightStatus;
  actualHeightMm: number;
  requiredHeightMm: number;
  deviationMm: number;
}

export interface StoreyHeightClassification {
  violations: ClassifiedStoreyHeight[];
  nearLimit: ClassifiedStoreyHeight[];
  compliant: ClassifiedStoreyHeight[];
  checked: number;
}

function stableId(lowerLevel: string, upperLevel: string): number {
  const text = `${lowerLevel}|${upperLevel}`;
  let hash = 0;
  for (let i = 0; i < text.length; i += 1) {
    hash = (hash * 31 + text.charCodeAt(i)) >>> 0;
  }
  return hash || 1;
}

/** Compute consecutive above-ground storey heights from level elevations (mm). */
export function computeStoreyHeights(levels: LevelInput[]): Array<{
  lowerLevel: string;
  upperLevel: string;
  heightMm: number;
}> {
  const aboveGround = levels
    .filter((level) => level.storeyKind === "aboveGround")
    .sort((a, b) => a.elevationMm - b.elevationMm);

  const pairs: Array<{
    lowerLevel: string;
    upperLevel: string;
    heightMm: number;
  }> = [];

  for (let i = 0; i < aboveGround.length - 1; i += 1) {
    const lower = aboveGround[i];
    const upper = aboveGround[i + 1];
    const heightMm = upper.elevationMm - lower.elevationMm;
    if (heightMm > 0) {
      pairs.push({
        lowerLevel: lower.levelName,
        upperLevel: upper.levelName,
        heightMm,
      });
    }
  }

  return pairs;
}

export function classifyStoreyHeights(
  levels: LevelInput[],
  options: {
    minStoreyHeightMm: number;
    nearLimitToleranceMm?: number;
  }
): StoreyHeightClassification {
  const tolerance = options.nearLimitToleranceMm ?? 50;
  const violations: ClassifiedStoreyHeight[] = [];
  const nearLimit: ClassifiedStoreyHeight[] = [];
  const compliant: ClassifiedStoreyHeight[] = [];

  const pairs = computeStoreyHeights(levels);

  for (const pair of pairs) {
    const deviationMm = options.minStoreyHeightMm - pair.heightMm;
    const classified: ClassifiedStoreyHeight = {
      id: stableId(pair.lowerLevel, pair.upperLevel),
      name: `${pair.lowerLevel} → ${pair.upperLevel}`,
      lowerLevel: pair.lowerLevel,
      upperLevel: pair.upperLevel,
      status: "compliant",
      actualHeightMm: pair.heightMm,
      requiredHeightMm: options.minStoreyHeightMm,
      deviationMm: deviationMm > 0 ? deviationMm : 0,
    };

    if (deviationMm <= 0) {
      compliant.push(classified);
    } else if (deviationMm <= tolerance) {
      classified.status = "nearLimit";
      nearLimit.push(classified);
    } else {
      classified.status = "violation";
      violations.push(classified);
    }
  }

  return {
    violations,
    nearLimit,
    compliant,
    checked: pairs.length,
  };
}
