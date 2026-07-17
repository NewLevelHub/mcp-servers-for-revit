import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  classifyRoutes,
  intraRoomPath,
  isExitRoom,
  MGN_DEAD_END_LIMIT_M,
  MGN_DEAD_END_SOURCE,
  pointInPolygon,
  segmentInsidePolygon,
  traceEvacuationRoutes,
  type EgressDoor,
  type EgressRoom,
} from "./evacuationDistance.js";

const rect = (x0: number, y0: number, x1: number, y1: number) => [
  { x: x0, y: y0 },
  { x: x1, y: y0 },
  { x: x1, y: y1 },
  { x: x0, y: y1 },
];

describe("geometry primitives", () => {
  const square = rect(0, 0, 10000, 10000);

  it("pointInPolygon", () => {
    assert.equal(pointInPolygon({ x: 5000, y: 5000 }, square), true);
    assert.equal(pointInPolygon({ x: 15000, y: 5000 }, square), false);
  });

  it("segmentInsidePolygon accepts interior segments and rejects crossing ones", () => {
    assert.equal(
      segmentInsidePolygon({ x: 1000, y: 1000 }, { x: 9000, y: 9000 }, square),
      true
    );
    // Segment leaving through the far wall (endpoints beyond tolerance).
    assert.equal(
      segmentInsidePolygon({ x: 5000, y: 5000 }, { x: 20000, y: 5000 }, square),
      false
    );
  });

  it("intraRoomPath detours around a concave (L-shaped) corridor", () => {
    // L-shape: horizontal leg 0..20000 x 0..2000, vertical leg 18000..20000 x 0..20000
    const lShape = [
      { x: 0, y: 0 },
      { x: 20000, y: 0 },
      { x: 20000, y: 20000 },
      { x: 18000, y: 20000 },
      { x: 18000, y: 2000 },
      { x: 0, y: 2000 },
    ];
    const a = { x: 500, y: 1000 };
    const b = { x: 19000, y: 19500 };

    const path = intraRoomPath(a, b, lShape);
    const straight = Math.hypot(b.x - a.x, b.y - a.y);

    assert.equal(path.direct, false, "путь не должен резать угол по прямой");
    assert.ok(path.points.length > 2, "маршрут должен содержать промежуточную точку у угла");
    assert.ok(
      path.lengthMm > straight + 1000,
      `детур должен быть заметно длиннее прямой (${Math.round(path.lengthMm)} vs ${Math.round(straight)})`
    );
  });
});

describe("traceEvacuationRoutes", () => {
  // Plan: room A — corridor — stairwell; corridor is a dead end past room B.
  //   A(0..5000 x 0..5000) door@(5000,2500) corridor(5000..25000 x 0..2000... )
  // Simple: corridor 5000..25000 x 0..5000? Keep rectangles adjoining.
  const rooms: EgressRoom[] = [
    {
      id: 1,
      name: "Квартира 1",
      centroid: { x: 2500, y: 2500 },
      boundary: rect(0, 0, 5000, 5000),
    },
    {
      id: 2,
      name: "Коридор",
      centroid: { x: 15000, y: 2500 },
      boundary: rect(5000, 0, 25000, 5000),
    },
    {
      id: 3,
      name: "Лестничная клетка",
      centroid: { x: 27000, y: 2500 },
      boundary: rect(25000, 0, 29000, 5000),
    },
  ];
  const doors: EgressDoor[] = [
    { id: 101, x: 5000, y: 2500, fromRoomId: 1, toRoomId: 2 },
    { id: 102, x: 25000, y: 2500, fromRoomId: 2, toRoomId: 3 },
  ];

  it("traces room → corridor → stairwell along geometry", () => {
    const result = traceEvacuationRoutes(rooms, doors);
    assert.equal(result.exitDoorIds.length, 1);
    assert.equal(result.routes.length, 1);

    const route = result.routes[0];
    assert.equal(route.roomId, 1);
    assert.equal(route.startDoorId, 101);
    assert.equal(route.exitDoorId, 102);
    // Door 101 → door 102 through the corridor: 20000 mm.
    assert.ok(Math.abs(route.lengthMm - 20000) < 200, `length ${route.lengthMm}`);
    assert.equal(route.corridorKind, "deadEnd", "единственный выход → тупиковый");
    assert.ok(route.polyline.length >= 2);
  });

  it("classifies through corridors when a second exit exists", () => {
    const doorsWithSecondExit: EgressDoor[] = [
      ...doors,
      // Exterior door at the far end of the corridor.
      { id: 103, x: 5500, y: 0, fromRoomId: 2, toRoomId: null, isExteriorWall: true },
    ];
    const result = traceEvacuationRoutes(rooms, doorsWithSecondExit);
    const route = result.routes.find((r) => r.roomId === 1);
    assert.ok(route);
    assert.equal(route!.reachableExits, 2);
    assert.equal(route!.corridorKind, "through");
  });

  it("reports rooms without a path and missing exits honestly", () => {
    const isolated: EgressRoom[] = [
      { id: 9, name: "Кладовая", centroid: { x: 500, y: 500 }, boundary: rect(0, 0, 1000, 1000) },
    ];
    const noExits = traceEvacuationRoutes(isolated, []);
    assert.equal(noExits.routes.length, 0);
    assert.ok(noExits.warnings.some((w) => w.includes("выходы не найдены")));
  });

  it("isExitRoom matches stairwell names, not corridors", () => {
    assert.equal(isExitRoom("Лестничная клетка ЛК-1"), true);
    assert.equal(isExitRoom("ЛК"), true);
    assert.equal(isExitRoom("Коридор"), false);
  });
});

describe("classifyRoutes", () => {
  const baseRoute = {
    roomId: 1,
    roomName: "Квартира",
    level: "1 этаж",
    startDoorId: 101,
    exitDoorId: 102,
    polyline: [],
    reachableExits: 1,
    corridorKind: "deadEnd" as const,
    hasDetours: false,
  };

  it("applies dead-end limit with the МГН preset values", () => {
    const routes = classifyRoutes(
      [
        { ...baseRoute, lengthMm: 20000 },
        { ...baseRoute, lengthMm: 14000 },
      ],
      { maxDeadEndM: MGN_DEAD_END_LIMIT_M, source: MGN_DEAD_END_SOURCE }
    );
    assert.equal(routes[0].status, "violation");
    assert.equal(routes[0].lengthM, 20);
    assert.equal(routes[0].deviationM, 5);
    assert.equal(routes[1].status, "compliant");
  });

  it("marks routes as measured when no limit applies", () => {
    const routes = classifyRoutes(
      [{ ...baseRoute, corridorKind: "through", lengthMm: 50000 }],
      { maxDeadEndM: 15 } // no through limit passed
    );
    assert.equal(routes[0].status, "measured");
  });

  it("МГН source cites СП РК 3.06-101-2012* п. 4.2.4 verbatim", () => {
    assert.match(MGN_DEAD_END_SOURCE.clause, /4\.2\.4/);
    assert.match(MGN_DEAD_END_SOURCE.quote, /не должно превышать 15 м/);
    assert.match(MGN_DEAD_END_SOURCE.quote, /тупиковый коридор/);
  });
});
