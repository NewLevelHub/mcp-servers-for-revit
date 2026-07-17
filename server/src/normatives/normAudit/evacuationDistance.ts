/**
 * Evacuation distance tracing (длина пути эвакуации до выхода).
 *
 * Pure geometry + graph logic over the egress graph exported from Revit
 * (export_egress_graph: room boundary polygons in mm, doors with from/to rooms).
 * Routes are traced along geometry: within a room the leg is a straight segment
 * only when it stays inside the room polygon; otherwise it detours through a
 * visibility graph over the polygon vertices. Room-to-room movement is via doors,
 * so the total is a walkable path length — never the straight-line distance.
 */

import type { NormAuditSource } from "./types.js";

export interface EgressPoint {
  x: number;
  y: number;
}

export interface EgressRoom {
  id: number;
  uniqueId?: string;
  name: string;
  number?: string;
  level?: string;
  centroid: EgressPoint;
  boundary: EgressPoint[];
}

export interface EgressDoor {
  id: number;
  uniqueId?: string;
  name?: string;
  level?: string;
  x: number;
  y: number;
  fromRoomId?: number | null;
  toRoomId?: number | null;
  widthMm?: number | null;
  isExteriorWall?: boolean;
}

/** МГН dead-end rule — the one limit we can cite verbatim from repo normatives. */
export const MGN_DEAD_END_SOURCE: NormAuditSource = {
  document: "СП РК 3.06-101-2012*",
  clause: "п. 4.2.4",
  quote:
    "В зданиях, с пребыванием маломобильных групп расстояние от дверей помещения, " +
    "выходящего в тупиковый коридор, до эвакуационного выхода с этажа не должно превышать 15 м.",
};
export const MGN_DEAD_END_LIMIT_M = 15;

/** Room-name tokens that mark an exit target (stairwell / stair lobby). */
export const EXIT_ROOM_TOKENS: readonly string[] = [
  "лестничная клетка",
  "лестница",
  "лк",
  "stair",
  "баспалдақ",
];

/** Start rooms: everything except corridors/exits themselves by default. */
const CORRIDOR_TOKENS: readonly string[] = ["коридор", "corridor", "дәліз", "холл", "hall"];

export function isExitRoom(name?: string, extraTokens: readonly string[] = []): boolean {
  const text = (name ?? "").toLowerCase().trim();
  if (!text) return false;
  const tokens = [...EXIT_ROOM_TOKENS, ...extraTokens.map((t) => t.toLowerCase())];
  return tokens.some((token) =>
    token.length <= 2 ? text === token : text.includes(token)
  );
}

export function isCorridorLikeRoom(name?: string): boolean {
  const text = (name ?? "").toLowerCase();
  return CORRIDOR_TOKENS.some((token) => text.includes(token));
}

// ---------------------------------------------------------------------------
// Geometry primitives (all mm)
// ---------------------------------------------------------------------------

const ENDPOINT_TOLERANCE_MM = 250;

function distance(a: EgressPoint, b: EgressPoint): number {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

export function pointInPolygon(point: EgressPoint, polygon: EgressPoint[]): boolean {
  let inside = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const pi = polygon[i];
    const pj = polygon[j];
    const intersects =
      pi.y > point.y !== pj.y > point.y &&
      point.x < ((pj.x - pi.x) * (point.y - pi.y)) / (pj.y - pi.y) + pi.x;
    if (intersects) inside = !inside;
  }
  return inside;
}

function orientation(p: EgressPoint, q: EgressPoint, r: EgressPoint): number {
  const value = (q.y - p.y) * (r.x - q.x) - (q.x - p.x) * (r.y - q.y);
  if (Math.abs(value) < 1e-9) return 0;
  return value > 0 ? 1 : 2;
}

function onSegment(p: EgressPoint, q: EgressPoint, r: EgressPoint): boolean {
  return (
    q.x <= Math.max(p.x, r.x) + 1e-9 &&
    q.x >= Math.min(p.x, r.x) - 1e-9 &&
    q.y <= Math.max(p.y, r.y) + 1e-9 &&
    q.y >= Math.min(p.y, r.y) - 1e-9
  );
}

function segmentsIntersect(
  p1: EgressPoint,
  q1: EgressPoint,
  p2: EgressPoint,
  q2: EgressPoint
): boolean {
  const o1 = orientation(p1, q1, p2);
  const o2 = orientation(p1, q1, q2);
  const o3 = orientation(p2, q2, p1);
  const o4 = orientation(p2, q2, q1);

  if (o1 !== o2 && o3 !== o4) return true;
  if (o1 === 0 && onSegment(p1, p2, q1)) return true;
  if (o2 === 0 && onSegment(p1, q2, q1)) return true;
  if (o3 === 0 && onSegment(p2, p1, q2)) return true;
  if (o4 === 0 && onSegment(p2, q1, q2)) return true;
  return false;
}

/**
 * True when the segment stays inside the polygon: no crossing of boundary edges
 * (crossings within ENDPOINT_TOLERANCE_MM of either endpoint are ignored — door
 * points sit in wall openings, slightly off the room boundary) and the midpoint
 * is inside.
 */
export function segmentInsidePolygon(
  a: EgressPoint,
  b: EgressPoint,
  polygon: EgressPoint[]
): boolean {
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const e1 = polygon[j];
    const e2 = polygon[i];
    if (!segmentsIntersect(a, b, e1, e2)) continue;

    // Ignore touches near the segment endpoints (door openings, shared vertices).
    const nearEndpoint =
      distancePointToSegment(a, e1, e2) < ENDPOINT_TOLERANCE_MM ||
      distancePointToSegment(b, e1, e2) < ENDPOINT_TOLERANCE_MM;
    if (!nearEndpoint) return false;
  }

  const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
  return pointInPolygon(mid, polygon) || distanceToPolygonBoundary(mid, polygon) < 50;
}

function distancePointToSegment(p: EgressPoint, a: EgressPoint, b: EgressPoint): number {
  const abx = b.x - a.x;
  const aby = b.y - a.y;
  const lengthSq = abx * abx + aby * aby;
  if (lengthSq < 1e-9) return distance(p, a);
  let t = ((p.x - a.x) * abx + (p.y - a.y) * aby) / lengthSq;
  t = Math.max(0, Math.min(1, t));
  return distance(p, { x: a.x + t * abx, y: a.y + t * aby });
}

function distanceToPolygonBoundary(p: EgressPoint, polygon: EgressPoint[]): number {
  let best = Infinity;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    best = Math.min(best, distancePointToSegment(p, polygon[j], polygon[i]));
  }
  return best;
}

/**
 * Shortest walkable path between two points inside a room polygon.
 * Straight when possible; otherwise Dijkstra over the visibility graph spanned
 * by the two points and the polygon vertices (pulled slightly inward so paths
 * round concave corners instead of hugging them).
 */
export function intraRoomPath(
  a: EgressPoint,
  b: EgressPoint,
  polygon: EgressPoint[]
): { points: EgressPoint[]; lengthMm: number; direct: boolean } {
  if (polygon.length < 3 || segmentInsidePolygon(a, b, polygon)) {
    return { points: [a, b], lengthMm: distance(a, b), direct: true };
  }

  const centroid = {
    x: polygon.reduce((sum, p) => sum + p.x, 0) / polygon.length,
    y: polygon.reduce((sum, p) => sum + p.y, 0) / polygon.length,
  };
  const inset = 100; // mm toward the room interior
  const waypoints = polygon.map((vertex) => {
    const d = distance(vertex, centroid);
    if (d < 1e-6) return vertex;
    return {
      x: vertex.x + ((centroid.x - vertex.x) / d) * inset,
      y: vertex.y + ((centroid.y - vertex.y) / d) * inset,
    };
  });

  const nodes: EgressPoint[] = [a, b, ...waypoints];
  const n = nodes.length;
  const dist = new Array<number>(n).fill(Infinity);
  const prev = new Array<number>(n).fill(-1);
  const visited = new Array<boolean>(n).fill(false);
  dist[0] = 0;

  const edgeLength = (i: number, j: number): number | null =>
    segmentInsidePolygon(nodes[i], nodes[j], polygon)
      ? distance(nodes[i], nodes[j])
      : null;

  for (let iter = 0; iter < n; iter++) {
    let u = -1;
    for (let i = 0; i < n; i++) {
      if (!visited[i] && (u === -1 || dist[i] < dist[u])) u = i;
    }
    if (u === -1 || dist[u] === Infinity) break;
    visited[u] = true;
    if (u === 1) break;

    for (let v = 0; v < n; v++) {
      if (visited[v]) continue;
      const length = edgeLength(u, v);
      if (length == null) continue;
      if (dist[u] + length < dist[v]) {
        dist[v] = dist[u] + length;
        prev[v] = u;
      }
    }
  }

  if (dist[1] === Infinity) {
    // Unreachable within the polygon (degenerate boundary) — honest fallback.
    return { points: [a, b], lengthMm: distance(a, b), direct: false };
  }

  const path: EgressPoint[] = [];
  for (let v = 1; v !== -1; v = prev[v]) path.push(nodes[v]);
  path.reverse();
  return { points: path, lengthMm: dist[1], direct: false };
}

// ---------------------------------------------------------------------------
// Egress graph and route tracing
// ---------------------------------------------------------------------------

export interface TracedRoute {
  roomId: number;
  roomName: string;
  roomNumber?: string;
  level: string;
  startDoorId: number;
  exitDoorId: number;
  /** Walkable route length door→…→exit, mm. */
  lengthMm: number;
  /** Route polyline in plan coordinates (mm) for detail-line drawing. */
  polyline: EgressPoint[];
  /** Number of distinct exits reachable from the start door. */
  reachableExits: number;
  /** deadEnd = only one exit reachable; through = two or more. */
  corridorKind: "deadEnd" | "through";
  /** True when at least one leg needed a non-direct polygon detour. */
  hasDetours: boolean;
}

export interface TraceOptions {
  /** Extra room-name tokens treated as exit targets (e.g. «вестибюль»). */
  exitRoomTokens?: string[];
  /** Trace from these rooms only (by name token); default: all non-corridor rooms. */
  startRoomTokens?: string[];
}

export interface TraceResult {
  routes: TracedRoute[];
  exitDoorIds: number[];
  startRoomCount: number;
  unreachableRooms: Array<{ roomId: number; roomName: string }>;
  warnings: string[];
}

interface DoorNode {
  door: EgressDoor;
  /** Room ids this door connects (1 for exterior doors). */
  roomIds: number[];
}

export function traceEvacuationRoutes(
  rooms: EgressRoom[],
  doors: EgressDoor[],
  options: TraceOptions = {}
): TraceResult {
  const warnings: string[] = [];
  const roomById = new Map(rooms.map((room) => [room.id, room]));

  const doorNodes: DoorNode[] = doors.map((door) => ({
    door,
    roomIds: [door.fromRoomId, door.toRoomId]
      .filter((id): id is number => id != null && roomById.has(id)),
  }));

  // Exit doors: lead outside through an exterior wall, or into a stairwell room.
  const exitTokens = options.exitRoomTokens ?? [];
  const exitDoorIndexes = new Set<number>();
  doorNodes.forEach((node, index) => {
    const { door } = node;
    const leadsOutside =
      door.isExteriorWall &&
      (door.fromRoomId == null || door.toRoomId == null) &&
      node.roomIds.length >= 1;
    const leadsToStair = [door.fromRoomId, door.toRoomId].some((id) => {
      if (id == null) return false;
      const room = roomById.get(id);
      return room ? isExitRoom(room.name, exitTokens) : false;
    });
    if (leadsOutside || leadsToStair) exitDoorIndexes.add(index);
  });

  if (exitDoorIndexes.size === 0) {
    warnings.push(
      "Эвакуационные выходы не найдены: нет дверей в наружных стенах и нет помещений " +
        "«лестничная клетка» — проверьте именование помещений или передайте exitRoomTokens."
    );
    return {
      routes: [],
      exitDoorIds: [],
      startRoomCount: 0,
      unreachableRooms: [],
      warnings,
    };
  }

  // Door-to-door edges within each room (walkable leg + its polyline).
  const doorsByRoom = new Map<number, number[]>();
  doorNodes.forEach((node, index) => {
    for (const roomId of node.roomIds) {
      const list = doorsByRoom.get(roomId) ?? [];
      list.push(index);
      doorsByRoom.set(roomId, list);
    }
  });

  interface Edge {
    to: number;
    lengthMm: number;
    polyline: EgressPoint[];
    direct: boolean;
  }
  const adjacency = new Map<number, Edge[]>();
  const addEdge = (from: number, edge: Edge) => {
    const list = adjacency.get(from) ?? [];
    list.push(edge);
    adjacency.set(from, list);
  };

  for (const [roomId, indexes] of doorsByRoom) {
    const room = roomById.get(roomId)!;
    for (let i = 0; i < indexes.length; i++) {
      for (let j = i + 1; j < indexes.length; j++) {
        const a = doorNodes[indexes[i]].door;
        const b = doorNodes[indexes[j]].door;
        const leg = intraRoomPath({ x: a.x, y: a.y }, { x: b.x, y: b.y }, room.boundary);
        addEdge(indexes[i], {
          to: indexes[j],
          lengthMm: leg.lengthMm,
          polyline: leg.points,
          direct: leg.direct,
        });
        addEdge(indexes[j], {
          to: indexes[i],
          lengthMm: leg.lengthMm,
          polyline: [...leg.points].reverse(),
          direct: leg.direct,
        });
      }
    }
  }

  // Multi-source Dijkstra from all exit doors (distance TO the nearest exit).
  const n = doorNodes.length;
  const dist = new Array<number>(n).fill(Infinity);
  const next = new Array<number>(n).fill(-1);
  const exitOf = new Array<number>(n).fill(-1);
  const visited = new Array<boolean>(n).fill(false);
  for (const exitIndex of exitDoorIndexes) {
    dist[exitIndex] = 0;
    exitOf[exitIndex] = exitIndex;
  }

  for (let iter = 0; iter < n; iter++) {
    let u = -1;
    for (let i = 0; i < n; i++) {
      if (!visited[i] && (u === -1 || dist[i] < dist[u])) u = i;
    }
    if (u === -1 || dist[u] === Infinity) break;
    visited[u] = true;
    for (const edge of adjacency.get(u) ?? []) {
      if (dist[u] + edge.lengthMm < dist[edge.to]) {
        dist[edge.to] = dist[u] + edge.lengthMm;
        next[edge.to] = u;
        exitOf[edge.to] = exitOf[u];
      }
    }
  }

  // Reachable-exit count per door (BFS over the unweighted graph per exit).
  const reachableExitsOf = new Array<Set<number>>(n);
  for (let i = 0; i < n; i++) reachableExitsOf[i] = new Set();
  for (const exitIndex of exitDoorIndexes) {
    const queue = [exitIndex];
    const seen = new Set<number>([exitIndex]);
    while (queue.length > 0) {
      const u = queue.pop()!;
      reachableExitsOf[u].add(exitIndex);
      for (const edge of adjacency.get(u) ?? []) {
        if (!seen.has(edge.to)) {
          seen.add(edge.to);
          queue.push(edge.to);
        }
      }
    }
  }

  // Start rooms: non-corridor, non-exit rooms (optionally filtered by tokens).
  const startTokens = options.startRoomTokens?.map((t) => t.toLowerCase());
  const startRooms = rooms.filter((room) => {
    if (isExitRoom(room.name, exitTokens)) return false;
    if (startTokens && startTokens.length > 0) {
      return startTokens.some((token) => room.name.toLowerCase().includes(token));
    }
    return !isCorridorLikeRoom(room.name);
  });

  const routes: TracedRoute[] = [];
  const unreachableRooms: Array<{ roomId: number; roomName: string }> = [];

  for (const room of startRooms) {
    const doorIndexes = doorsByRoom.get(room.id) ?? [];
    if (doorIndexes.length === 0) continue;

    // По норме расстояние считается от двери помещения — берём худшую (дальнюю) дверь.
    let bestIndex = -1;
    for (const index of doorIndexes) {
      if (dist[index] === Infinity) continue;
      if (bestIndex === -1 || dist[index] > dist[bestIndex]) bestIndex = index;
    }
    if (bestIndex === -1) {
      unreachableRooms.push({ roomId: room.id, roomName: room.name });
      continue;
    }

    // Reconstruct the polyline door → exit.
    const polyline: EgressPoint[] = [];
    let hasDetours = false;
    let u = bestIndex;
    while (u !== -1 && exitOf[u] !== u) {
      const v = next[u];
      const edge = (adjacency.get(u) ?? []).find(
        (candidate) =>
          candidate.to === v &&
          Math.abs(dist[u] - candidate.lengthMm - dist[v]) < 1
      );
      if (!edge) break;
      if (!edge.direct) hasDetours = true;
      const points = edge.polyline;
      if (polyline.length === 0) polyline.push(...points);
      else polyline.push(...points.slice(1));
      u = v;
    }

    const exits = reachableExitsOf[bestIndex];
    routes.push({
      roomId: room.id,
      roomName: room.name,
      roomNumber: room.number,
      level: room.level ?? "",
      startDoorId: doorNodes[bestIndex].door.id,
      exitDoorId: exitOf[bestIndex] >= 0 ? doorNodes[exitOf[bestIndex]].door.id : -1,
      lengthMm: Math.round(dist[bestIndex]),
      polyline,
      reachableExits: exits.size,
      corridorKind: exits.size >= 2 ? "through" : "deadEnd",
      hasDetours,
    });
  }

  routes.sort((a, b) => b.lengthMm - a.lengthMm);

  return {
    routes,
    exitDoorIds: [...exitDoorIndexes].map((index) => doorNodes[index].door.id),
    startRoomCount: startRooms.length,
    unreachableRooms,
    warnings,
  };
}

// ---------------------------------------------------------------------------
// Norm comparison
// ---------------------------------------------------------------------------

export interface DistanceLimits {
  maxDeadEndM?: number;
  maxThroughM?: number;
  source?: NormAuditSource;
}

export type RouteStatus = "violation" | "nearLimit" | "compliant" | "measured";

export interface ClassifiedRoute extends TracedRoute {
  lengthM: number;
  limitM?: number;
  status: RouteStatus;
  deviationM?: number;
}

export function classifyRoutes(
  routes: TracedRoute[],
  limits: DistanceLimits,
  nearLimitToleranceM = 1
): ClassifiedRoute[] {
  return routes.map((route) => {
    const lengthM = Math.round(route.lengthMm / 100) / 10;
    const limitM =
      route.corridorKind === "deadEnd" ? limits.maxDeadEndM : limits.maxThroughM;

    if (limitM == null) {
      return { ...route, lengthM, status: "measured" as const };
    }

    const deviationM = Math.round((lengthM - limitM) * 10) / 10;
    let status: RouteStatus = "compliant";
    if (deviationM > 0) {
      status = deviationM <= nearLimitToleranceM ? "nearLimit" : "violation";
    }
    return { ...route, lengthM, limitM, status, deviationM: Math.max(0, deviationM) };
  });
}
