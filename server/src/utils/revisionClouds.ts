import { createHash } from "node:crypto";

/**
 * Turning a diff into облака изменений — one cloud per cluster of nearby
 * changes, not one per element (REV-172).
 *
 * Pure geometry over the `location` (mm, bounding-box centre) that
 * `compare_model_versions` (REV-171) already carries on every change. Nothing
 * here touches Revit: the plugin command gets a finished list of rectangles and
 * only has to find a view and draw. That split is what makes "8 changes in one
 * room → one cloud" a rule exercised in a test, not a rule read off a screenshot.
 */

export interface ChangeLocation {
  elementId: number;
  uniqueId: string;
  /** Which level's plan the cloud belongs on. */
  level: string;
  /** Short human label, folded into the cloud's comment for a person reading Revit directly. */
  label: string;
  /** Bounding-box centre, mm. */
  x: number;
  y: number;
}

export interface CloudCluster {
  level: string;
  changeCount: number;
  elementIds: number[];
  uniqueIds: string[];
  /** A handful of labels for the comment — capped, see {@link MAX_LABELS_IN_CLUSTER}. */
  labels: string[];
  labelsTruncated: boolean;
  /** The cluster's own rectangle, mm, no margin — for a caller that wants the raw extent. */
  boundsMm: { minX: number; minY: number; maxX: number; maxY: number };
  /** What the cloud is actually drawn around — `boundsMm` expanded by the margin. */
  cloudBoundsMm: { minX: number; minY: number; maxX: number; maxY: number };
  /**
   * Stable identity of "this exact set of elements, on this exact level", used
   * to recognise a cluster a previous run already drew a cloud for. Two runs
   * over the same diff produce the same signature for the same room's changes
   * even if the changes arrived in a different order.
   */
  signature: string;
}

export interface ClusterOptions {
  /** Changes within this distance of each other (chained, not just pairwise to one centre) join one cloud. */
  radiusMm?: number;
  /** How far the cloud is drawn past the cluster's own bounding rectangle. */
  marginMm?: number;
}

/** One room's worth of changes is typically within a few metres of each other. */
export const DEFAULT_CLUSTER_RADIUS_MM = 3000;
/** Enough that the cloud does not hug the element it is pointing at. */
export const DEFAULT_CLOUD_MARGIN_MM = 500;
/** Hex characters kept from the signature hash — same order as `modelSnapshot.ts`'s HASH_LENGTH. */
const SIGNATURE_LENGTH = 16;
/** Labels beyond this are folded into "и ещё N" rather than listed — same idea as `modelDiff.ts`'s clause folding. */
const MAX_LABELS_IN_CLUSTER = 5;

function distanceMm(a: { x: number; y: number }, b: { x: number; y: number }): number {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

/**
 * Stable identity of a cluster: a hash of its member UniqueIds, sorted so
 * membership — not arrival order — is what matters. Two elements changing
 * together always fold into the same cloud, and re-running the exact same diff
 * reproduces the exact same signatures, which is what lets the Revit side skip
 * a cluster it already drew.
 */
export function cloudSignature(uniqueIds: readonly string[]): string {
  const sorted = [...uniqueIds].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
  return createHash("sha1").update(sorted.join(""), "utf8").digest("hex").slice(0, SIGNATURE_LENGTH);
}

/** Union-find over one level's points, joined by `radiusMm` — single-link, so a chain of near neighbours is one cluster. */
function connectedComponents(points: ChangeLocation[], radiusMm: number): number[][] {
  const parent = points.map((_, i) => i);

  function find(i: number): number {
    while (parent[i] !== i) {
      parent[i] = parent[parent[i]];
      i = parent[i];
    }
    return i;
  }

  function union(a: number, b: number): void {
    const rootA = find(a);
    const rootB = find(b);
    if (rootA !== rootB) parent[rootA] = rootB;
  }

  for (let i = 0; i < points.length; i++) {
    for (let j = i + 1; j < points.length; j++) {
      if (distanceMm(points[i], points[j]) <= radiusMm) union(i, j);
    }
  }

  const groups = new Map<number, number[]>();
  for (let i = 0; i < points.length; i++) {
    const root = find(i);
    let group = groups.get(root);
    if (!group) {
      group = [];
      groups.set(root, group);
    }
    group.push(i);
  }

  return [...groups.values()];
}

/**
 * A cloud narrower or shorter than this is a zero-width rectangle waiting to
 * happen — a single-point cluster with `marginMm: 0` is a legal call, and
 * `RevisionCloud.Create` throws on a zero-length curve rather than drawing a
 * thin sliver. Half a metre reads as "one element", not as a mistake.
 */
const MIN_CLOUD_DIMENSION_MM = 200;

function buildCluster(level: string, members: ChangeLocation[], marginMm: number): CloudCluster {
  const xs = members.map((m) => m.x);
  const ys = members.map((m) => m.y);
  const boundsMm = { minX: Math.min(...xs), minY: Math.min(...ys), maxX: Math.max(...xs), maxY: Math.max(...ys) };

  const labels = members.map((m) => m.label).filter(Boolean);
  const uniqueIds = members.map((m) => m.uniqueId);

  let minX = boundsMm.minX - marginMm;
  let maxX = boundsMm.maxX + marginMm;
  let minY = boundsMm.minY - marginMm;
  let maxY = boundsMm.maxY + marginMm;

  if (maxX - minX < MIN_CLOUD_DIMENSION_MM) {
    const centerX = (minX + maxX) / 2;
    minX = centerX - MIN_CLOUD_DIMENSION_MM / 2;
    maxX = centerX + MIN_CLOUD_DIMENSION_MM / 2;
  }
  if (maxY - minY < MIN_CLOUD_DIMENSION_MM) {
    const centerY = (minY + maxY) / 2;
    minY = centerY - MIN_CLOUD_DIMENSION_MM / 2;
    maxY = centerY + MIN_CLOUD_DIMENSION_MM / 2;
  }

  return {
    level,
    changeCount: members.length,
    elementIds: members.map((m) => m.elementId),
    uniqueIds,
    labels: labels.slice(0, MAX_LABELS_IN_CLUSTER),
    labelsTruncated: labels.length > MAX_LABELS_IN_CLUSTER,
    boundsMm,
    cloudBoundsMm: { minX, minY, maxX, maxY },
    signature: cloudSignature(uniqueIds),
  };
}

/**
 * Changes → clouds. Grouped by level first — a cloud never spans two levels,
 * each gets its own plan — then clustered within the level by proximity.
 * Busiest cluster first within each level, levels in the order they first
 * appear (`compare_model_versions` already sorts by how much changed).
 */
export function clusterChangeLocations(
  changes: readonly ChangeLocation[],
  options: ClusterOptions = {}
): CloudCluster[] {
  const radiusMm = options.radiusMm ?? DEFAULT_CLUSTER_RADIUS_MM;
  const marginMm = options.marginMm ?? DEFAULT_CLOUD_MARGIN_MM;

  const levelOrder: string[] = [];
  const byLevel = new Map<string, ChangeLocation[]>();
  for (const change of changes) {
    if (!byLevel.has(change.level)) {
      byLevel.set(change.level, []);
      levelOrder.push(change.level);
    }
    byLevel.get(change.level)!.push(change);
  }

  const clusters: CloudCluster[] = [];
  for (const level of levelOrder) {
    const points = byLevel.get(level)!;
    const groups = connectedComponents(points, radiusMm);
    for (const indices of groups) {
      clusters.push(buildCluster(level, indices.map((i) => points[i]), marginMm));
    }
  }

  return clusters.sort((a, b) => {
    if (a.level !== b.level) return levelOrder.indexOf(a.level) - levelOrder.indexOf(b.level);
    return b.changeCount - a.changeCount;
  });
}

/** One line for the cloud's own comment/tooltip — what an architect reads in Revit, not just in the tool's answer. */
export function describeCluster(cluster: CloudCluster): string {
  const shown = cluster.labels.join(", ");
  const rest = cluster.labelsTruncated ? ` и ещё ${cluster.changeCount - cluster.labels.length}` : "";
  return `Изменений: ${cluster.changeCount} — ${shown}${rest}.`;
}
