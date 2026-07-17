/**
 * Pure classifiers for REV-59 vertical circulation checks.
 */

export type LinearStatus = "violation" | "nearLimit" | "compliant";

export interface StairWidthInput {
  id: number;
  uniqueId?: string;
  name?: string;
  type?: string;
  level?: string;
  widthMm?: number | null;
}

export interface ClassifiedStairWidth {
  id: number;
  uniqueId?: string;
  name: string;
  type: string;
  level: string;
  status: LinearStatus;
  actualMm: number;
  requiredMm: number;
  deviationMm: number;
}

export function classifyStairWidths(
  stairs: StairWidthInput[],
  options: { minWidthMm: number; nearLimitToleranceMm?: number }
): {
  violations: ClassifiedStairWidth[];
  nearLimit: ClassifiedStairWidth[];
  compliant: ClassifiedStairWidth[];
  checked: number;
  missingWidth: number;
} {
  const tolerance = options.nearLimitToleranceMm ?? 50;
  const violations: ClassifiedStairWidth[] = [];
  const nearLimit: ClassifiedStairWidth[] = [];
  const compliant: ClassifiedStairWidth[] = [];
  let missingWidth = 0;

  for (const stair of stairs) {
    const width = stair.widthMm;
    if (width == null || !Number.isFinite(width) || width <= 0) {
      missingWidth += 1;
      continue;
    }
    const deviationMm = options.minWidthMm - width;
    const item: ClassifiedStairWidth = {
      id: stair.id,
      uniqueId: stair.uniqueId,
      name: stair.name || stair.type || `лестница ${stair.id}`,
      type: stair.type ?? "",
      level: stair.level ?? "",
      status: "compliant",
      actualMm: width,
      requiredMm: options.minWidthMm,
      deviationMm: deviationMm > 0 ? deviationMm : 0,
    };
    if (deviationMm <= 0) {
      compliant.push(item);
    } else if (deviationMm <= tolerance) {
      item.status = "nearLimit";
      nearLimit.push(item);
    } else {
      item.status = "violation";
      violations.push(item);
    }
  }

  return {
    violations,
    nearLimit,
    compliant,
    checked: violations.length + nearLimit.length + compliant.length,
    missingWidth,
  };
}

export interface StairRiserTreadInput {
  id: number;
  uniqueId?: string;
  name?: string;
  type?: string;
  level?: string;
  riserMm?: number | null;
  treadMm?: number | null;
}

export interface ClassifiedStairRiserTread {
  id: number;
  uniqueId?: string;
  name: string;
  type: string;
  level: string;
  metric: "подступенок" | "проступь";
  status: LinearStatus;
  actualMm: number;
  requiredMm: number;
  deviationMm: number;
}

/**
 * Riser: violation when actual > maxRiserMm (too tall).
 * Tread: violation when actual < minTreadMm (too shallow).
 */
export function classifyStairRiserTreads(
  stairs: StairRiserTreadInput[],
  options: {
    maxRiserMm?: number | null;
    minTreadMm?: number | null;
    nearLimitToleranceMm?: number;
  }
): {
  violations: ClassifiedStairRiserTread[];
  nearLimit: ClassifiedStairRiserTread[];
  compliant: ClassifiedStairRiserTread[];
  checked: number;
  missing: number;
} {
  const tolerance = options.nearLimitToleranceMm ?? 10;
  const violations: ClassifiedStairRiserTread[] = [];
  const nearLimit: ClassifiedStairRiserTread[] = [];
  const compliant: ClassifiedStairRiserTread[] = [];
  let missing = 0;

  for (const stair of stairs) {
    const name = stair.name || stair.type || `лестница ${stair.id}`;
    let any = false;

    if (options.maxRiserMm != null && options.maxRiserMm > 0) {
      const riser = stair.riserMm;
      if (riser == null || !Number.isFinite(riser) || riser <= 0) {
        missing += 1;
      } else {
        any = true;
        const over = riser - options.maxRiserMm;
        const item: ClassifiedStairRiserTread = {
          id: stair.id,
          uniqueId: stair.uniqueId,
          name,
          type: stair.type ?? "",
          level: stair.level ?? "",
          metric: "подступенок",
          status: "compliant",
          actualMm: riser,
          requiredMm: options.maxRiserMm,
          deviationMm: over > 0 ? over : 0,
        };
        if (over <= 0) compliant.push(item);
        else if (over <= tolerance) {
          item.status = "nearLimit";
          nearLimit.push(item);
        } else {
          item.status = "violation";
          violations.push(item);
        }
      }
    }

    if (options.minTreadMm != null && options.minTreadMm > 0) {
      const tread = stair.treadMm;
      if (tread == null || !Number.isFinite(tread) || tread <= 0) {
        missing += 1;
      } else {
        any = true;
        const shortfall = options.minTreadMm - tread;
        const item: ClassifiedStairRiserTread = {
          id: stair.id,
          uniqueId: stair.uniqueId,
          name,
          type: stair.type ?? "",
          level: stair.level ?? "",
          metric: "проступь",
          status: "compliant",
          actualMm: tread,
          requiredMm: options.minTreadMm,
          deviationMm: shortfall > 0 ? shortfall : 0,
        };
        if (shortfall <= 0) compliant.push(item);
        else if (shortfall <= tolerance) {
          item.status = "nearLimit";
          nearLimit.push(item);
        } else {
          item.status = "violation";
          violations.push(item);
        }
      }
    }

    if (!any && options.maxRiserMm == null && options.minTreadMm == null) {
      // nothing configured
    }
  }

  return {
    violations,
    nearLimit,
    compliant,
    checked: violations.length + nearLimit.length + compliant.length,
    missing,
  };
}

export interface RampInput {
  id: number;
  uniqueId?: string;
  name?: string;
  type?: string;
  level?: string;
  widthMm?: number | null;
  slopePercent?: number | null;
}

export interface ClassifiedRamp {
  id: number;
  uniqueId?: string;
  name: string;
  type: string;
  level: string;
  metric: "ширина" | "уклон";
  status: LinearStatus;
  actualMm: number;
  requiredMm: number;
  deviationMm: number;
}

/**
 * Width: min. Slope: max percent (stored in actualMm/requiredMm as percent points).
 */
export function classifyRamps(
  ramps: RampInput[],
  options: {
    minWidthMm?: number | null;
    maxSlopePercent?: number | null;
    nearLimitToleranceMm?: number;
    nearLimitTolerancePercent?: number;
  }
): {
  violations: ClassifiedRamp[];
  nearLimit: ClassifiedRamp[];
  compliant: ClassifiedRamp[];
  checked: number;
  missing: number;
} {
  const tolMm = options.nearLimitToleranceMm ?? 50;
  const tolPct = options.nearLimitTolerancePercent ?? 0.5;
  const violations: ClassifiedRamp[] = [];
  const nearLimit: ClassifiedRamp[] = [];
  const compliant: ClassifiedRamp[] = [];
  let missing = 0;

  const push = (item: ClassifiedRamp) => {
    if (item.status === "violation") violations.push(item);
    else if (item.status === "nearLimit") nearLimit.push(item);
    else compliant.push(item);
  };

  for (const ramp of ramps) {
    const name = ramp.name || ramp.type || `пандус ${ramp.id}`;

    if (options.minWidthMm != null && options.minWidthMm > 0) {
      const width = ramp.widthMm;
      if (width == null || !Number.isFinite(width) || width <= 0) {
        missing += 1;
      } else {
        const shortfall = options.minWidthMm - width;
        const item: ClassifiedRamp = {
          id: ramp.id,
          uniqueId: ramp.uniqueId,
          name,
          type: ramp.type ?? "",
          level: ramp.level ?? "",
          metric: "ширина",
          status: "compliant",
          actualMm: width,
          requiredMm: options.minWidthMm,
          deviationMm: shortfall > 0 ? shortfall : 0,
        };
        if (shortfall <= 0) push(item);
        else if (shortfall <= tolMm) {
          item.status = "nearLimit";
          push(item);
        } else {
          item.status = "violation";
          push(item);
        }
      }
    }

    if (options.maxSlopePercent != null && options.maxSlopePercent > 0) {
      const slope = ramp.slopePercent;
      if (slope == null || !Number.isFinite(slope) || slope < 0) {
        missing += 1;
      } else {
        const over = slope - options.maxSlopePercent;
        const item: ClassifiedRamp = {
          id: ramp.id,
          uniqueId: ramp.uniqueId,
          name,
          type: ramp.type ?? "",
          level: ramp.level ?? "",
          metric: "уклон",
          status: "compliant",
          actualMm: slope,
          requiredMm: options.maxSlopePercent,
          deviationMm: over > 0 ? over : 0,
        };
        if (over <= 0) push(item);
        else if (over <= tolPct) {
          item.status = "nearLimit";
          push(item);
        } else {
          item.status = "violation";
          push(item);
        }
      }
    }
  }

  return {
    violations,
    nearLimit,
    compliant,
    checked: violations.length + nearLimit.length + compliant.length,
    missing,
  };
}

export interface RailingInput {
  id: number;
  uniqueId?: string;
  name?: string;
  type?: string;
  level?: string;
  heightMm?: number | null;
}

export interface ClassifiedRailing {
  id: number;
  uniqueId?: string;
  name: string;
  type: string;
  level: string;
  status: LinearStatus;
  actualMm: number;
  requiredMm: number;
  deviationMm: number;
}

/**
 * Parse «h 1200», «h1200» from ADSK type/instance names when param is missing.
 */
export function parseRailingHeightMmFromName(
  ...parts: Array<string | null | undefined>
): number | null {
  const blob = parts.filter(Boolean).join(" ");
  if (!blob) return null;
  const match =
    blob.match(/(?:^|[_\s\-])(?:h|H|н|Н)\s*[=:]?\s*(\d{3,4})(?:\b|$)/) ||
    blob.match(/(?:высота|Высота)\s*[=:]?\s*(\d{3,4})/);
  if (!match) return null;
  const mm = Number(match[1]);
  if (!Number.isFinite(mm) || mm < 400 || mm > 2000) return null;
  return mm;
}

function isRailingAccessory(name: string, type: string): boolean {
  const blob = `${name} ${type}`.toLowerCase();
  return (
    blob.includes("накрывоч") ||
    blob.includes("cover") ||
    blob.includes("cap ")
  );
}

/**
 * МГН / поручни 0.8–0.9 м — не путать с мин. высотой балконного ограждения ≥ 1.15–1.2 м.
 */
function isMgnHandrail(name: string, type: string, heightMm: number): boolean {
  const blob = `${name} ${type}`.toLowerCase();
  if (blob.includes("мгн")) return true;
  if (heightMm >= 800 && heightMm <= 950 && blob.includes("поруч")) return true;
  return false;
}

/** Railing height: violation when actual < minHeightMm. */
export function classifyRailingHeights(
  railings: RailingInput[],
  options: { minHeightMm: number; nearLimitToleranceMm?: number }
): {
  violations: ClassifiedRailing[];
  nearLimit: ClassifiedRailing[];
  compliant: ClassifiedRailing[];
  checked: number;
  missingHeight: number;
  skippedHandrails: number;
  skippedAccessories: number;
} {
  const tolerance = options.nearLimitToleranceMm ?? 50;
  const violations: ClassifiedRailing[] = [];
  const nearLimit: ClassifiedRailing[] = [];
  const compliant: ClassifiedRailing[] = [];
  let missingHeight = 0;
  let skippedHandrails = 0;
  let skippedAccessories = 0;

  for (const railing of railings) {
    const name = railing.name || railing.type || `ограждение ${railing.id}`;
    const type = railing.type ?? "";
    if (isRailingAccessory(name, type)) {
      skippedAccessories += 1;
      continue;
    }

    let height = railing.heightMm;
    if (height == null || !Number.isFinite(height) || height <= 0) {
      height = parseRailingHeightMmFromName(railing.name, railing.type);
    }
    if (height == null || !Number.isFinite(height) || height <= 0) {
      missingHeight += 1;
      continue;
    }

    // Balcony/fire railing norms (≥ ~1.15 m): do not flag МГН handrails (0.8–0.9).
    if (options.minHeightMm >= 1100 && isMgnHandrail(name, type, height)) {
      skippedHandrails += 1;
      continue;
    }

    const shortfall = options.minHeightMm - height;
    const item: ClassifiedRailing = {
      id: railing.id,
      uniqueId: railing.uniqueId,
      name,
      type,
      level: railing.level ?? "",
      status: "compliant",
      actualMm: height,
      requiredMm: options.minHeightMm,
      deviationMm: shortfall > 0 ? shortfall : 0,
    };
    if (shortfall <= 0) compliant.push(item);
    else if (shortfall <= tolerance) {
      item.status = "nearLimit";
      nearLimit.push(item);
    } else {
      item.status = "violation";
      violations.push(item);
    }
  }

  return {
    violations,
    nearLimit,
    compliant,
    checked: violations.length + nearLimit.length + compliant.length,
    missingHeight,
    skippedHandrails,
    skippedAccessories,
  };
}
