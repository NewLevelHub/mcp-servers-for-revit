import test from "node:test";
import assert from "node:assert/strict";
import type { SnapshotElementRow } from "./modelSnapshot.js";
import {
  buildDiffHeadline,
  countChanges,
  describeChange,
  diffSnapshotElements,
  formatParameterValue,
  groupChanges,
  VOLATILE_PARAMETER_KEYS,
} from "./modelDiff.js";

/**
 * The rule that decides what a diff shows an architect (REV-171).
 *
 * A moved wall has to survive as one "moved" entry, not "removed" + "added"; a
 * recomputed area has to disappear unless it is a room's own; and a 400-row
 * model has to fold into a sentence, not a table. All three are exercised here
 * against synthetic rows, same as `modelSnapshot.test.ts` (REV-170).
 */

let nextId = 1;

function row(overrides: Partial<SnapshotElementRow> & { uniqueId: string }): SnapshotElementRow {
  const id = nextId++;
  return {
    elementId: id,
    categoryKey: "OST_Walls",
    category: "Стены",
    familyName: "Базовая стена",
    typeName: "Кладка 250",
    typeId: 100,
    levelName: "3 этаж",
    roomName: "",
    roomNumber: "",
    bboxMinX: 0,
    bboxMinY: 0,
    bboxMinZ: 0,
    bboxMaxX: 1000,
    bboxMaxY: 200,
    bboxMaxZ: 3000,
    paramHash: "h",
    paramsJson: "{}",
    ...overrides,
  };
}

function withParams(base: SnapshotElementRow, params: Record<string, string>): SnapshotElementRow {
  return { ...base, paramsJson: JSON.stringify(params) };
}

// --- matching: moved, not removed+added -------------------------------------

test("a wall dragged sideways is one 'moved' entry, not removed+added", () => {
  const before = [row({ uniqueId: "w1" })];
  const after = [row({ uniqueId: "w1", bboxMinX: 2000, bboxMaxX: 3000 })];

  const changes = diffSnapshotElements(before, after);

  assert.equal(changes.length, 1);
  assert.equal(changes[0].kind, "modified");
  assert.equal(changes[0].moved, true);
  assert.ok((changes[0].moveDistanceMm ?? 0) > 1000);
});

test("a shift smaller than the tolerance is not a move", () => {
  const before = [row({ uniqueId: "w1" })];
  const after = [row({ uniqueId: "w1", bboxMinX: 1, bboxMaxX: 1001 })];

  const changes = diffSnapshotElements(before, after, {}, { moveToleranceMm: 5 });

  assert.equal(changes.length, 0);
});

test("a genuinely new uniqueId is added, a vanished one is removed", () => {
  const before = [row({ uniqueId: "w1" })];
  const after = [row({ uniqueId: "w2" })];

  const changes = diffSnapshotElements(before, after);
  const kinds = changes.map((c) => c.kind).sort();

  assert.deepEqual(kinds, ["added", "removed"]);
});

test("an untouched element produces no entry at all", () => {
  const rows = [row({ uniqueId: "w1" })];
  const changes = diffSnapshotElements(rows, rows.map((r) => ({ ...r })));

  assert.deepEqual(changes, []);
});

// --- noise filtering ----------------------------------------------------------

test("a recomputed area with nothing else different is not a change", () => {
  const before = [
    withParams(row({ uniqueId: "f1", categoryKey: "OST_Floors", category: "Перекрытия" }), {
      HOST_AREA_COMPUTED: "100",
    }),
  ];
  const after = [
    withParams(row({ uniqueId: "f1", categoryKey: "OST_Floors", category: "Перекрытия" }), {
      HOST_AREA_COMPUTED: "104.3",
    }),
  ];

  const changes = diffSnapshotElements(before, after);

  assert.deepEqual(changes, [], "HOST_AREA_COMPUTED alone must not surface a change");
});

test("every volatile key is dropped even when other volatile keys also moved", () => {
  const before = [
    withParams(row({ uniqueId: "s1" }), {
      CURVE_ELEM_LENGTH: "10",
      HOST_VOLUME_COMPUTED: "5",
      ROOM_PERIMETER: "20",
    }),
  ];
  const after = [
    withParams(row({ uniqueId: "s1" }), {
      CURVE_ELEM_LENGTH: "11",
      HOST_VOLUME_COMPUTED: "5.4",
      ROOM_PERIMETER: "21",
    }),
  ];

  assert.deepEqual(diffSnapshotElements(before, after), []);
  for (const key of ["CURVE_ELEM_LENGTH", "HOST_VOLUME_COMPUTED", "ROOM_PERIMETER"]) {
    assert.ok(VOLATILE_PARAMETER_KEYS.has(key));
  }
});

test("a room's own area IS reported — the whole эпик exists for this sentence", () => {
  const before = [
    withParams(
      row({
        uniqueId: "r1",
        categoryKey: "OST_Rooms",
        category: "Помещения",
        roomNumber: "45",
        roomName: "Кухня",
      }),
      { ROOM_AREA: "484" } // ~45 m²
    ),
  ];
  const after = [
    withParams(
      row({
        uniqueId: "r1",
        categoryKey: "OST_Rooms",
        category: "Помещения",
        roomNumber: "45",
        roomName: "Кухня",
      }),
      { ROOM_AREA: "527" } // ~49 m²
    ),
  ];

  const changes = diffSnapshotElements(before, after);

  assert.equal(changes.length, 1);
  const areaChange = changes[0].changedParameters.find((p) => p.key === "ROOM_AREA");
  assert.ok(areaChange, "ROOM_AREA must survive into changedParameters");
  assert.match(areaChange!.newValue ?? "", /м²/);
});

// --- a real edit still comes through -----------------------------------------

test("a mark change is reported with old and new value", () => {
  const before = [withParams(row({ uniqueId: "w1" }), { ALL_MODEL_MARK: "Ст-1" })];
  const after = [withParams(row({ uniqueId: "w1" }), { ALL_MODEL_MARK: "Ст-2" })];

  const changes = diffSnapshotElements(before, after);

  assert.equal(changes.length, 1);
  assert.deepEqual(changes[0].changedParameters[0], {
    key: "ALL_MODEL_MARK",
    label: "ALL_MODEL_MARK",
    oldValue: "Ст-1",
    newValue: "Ст-2",
    oldRaw: "Ст-1",
    newRaw: "Ст-2",
  });
});

test("a length parameter is converted to millimetres for the reader", () => {
  assert.equal(formatParameterValue("WALL_BASE_OFFSET", "3.2808398950131235"), "1000 мм");
  assert.equal(formatParameterValue("ALL_MODEL_MARK", "Ст-1"), "Ст-1");
  assert.equal(formatParameterValue("WALL_BASE_OFFSET", undefined), null);
});

test("a level change and a room change are both flagged, independent of geometry", () => {
  const before = [row({ uniqueId: "d1", categoryKey: "OST_Doors", category: "Двери", roomName: "Спальня" })];
  const after = [
    row({
      uniqueId: "d1",
      categoryKey: "OST_Doors",
      category: "Двери",
      levelName: "4 этаж",
      roomName: "Кухня",
    }),
  ];

  const changes = diffSnapshotElements(before, after);

  assert.equal(changes.length, 1);
  assert.equal(changes[0].levelChanged, true);
  assert.equal(changes[0].oldLevel, "3 этаж");
  assert.equal(changes[0].roomChanged, true);
});

// --- grouping -----------------------------------------------------------------

test("changes group by level, then by room, busiest first", () => {
  const changes = diffSnapshotElements(
    [row({ uniqueId: "a" }), row({ uniqueId: "b" }), row({ uniqueId: "c", levelName: "2 этаж" })],
    [
      row({ uniqueId: "a", bboxMinX: 5000, bboxMaxX: 6000, roomNumber: "45", roomName: "Кухня" }),
      row({ uniqueId: "b", bboxMinX: 5000, bboxMaxX: 6000, roomNumber: "46", roomName: "Спальня" }),
      row({ uniqueId: "c", levelName: "2 этаж", bboxMinX: 5000, bboxMaxX: 6000 }),
    ]
  );

  const groups = groupChanges(changes);

  assert.equal(groups[0].level, "3 этаж");
  assert.equal(groups[0].count, 2);
  assert.equal(groups[1].level, "2 этаж");
  assert.equal(groups[1].count, 1);
});

// --- text -----------------------------------------------------------------

test("describeChange reads as one sentence, gender-neutral", () => {
  const before = [row({ uniqueId: "w1" })];
  const after = [row({ uniqueId: "w1", bboxMinX: 2000, bboxMaxX: 3000 })];
  const [change] = diffSnapshotElements(before, after);

  const text = describeChange(change);
  assert.match(text, /смещение \d+(\.\d+)? мм/);
  assert.match(text, /^Стены «Кладка 250» \(id \d+\):/);
});

test("describeChange marks added and removed elements plainly", () => {
  const added = diffSnapshotElements([], [row({ uniqueId: "w1" })]);
  const removed = diffSnapshotElements([row({ uniqueId: "w1" })], []);

  assert.match(describeChange(added[0]), /^Добавлено:/);
  assert.match(describeChange(removed[0]), /^Удалено:/);
});

// --- headline -----------------------------------------------------------------

test("buildDiffHeadline reports 'no changes' plainly", () => {
  assert.equal(buildDiffHeadline([]), "Изменений нет.");
});

test("buildDiffHeadline clusters moved walls by level and names the room area swing", () => {
  const beforeWalls = Array.from({ length: 12 }, (_, i) => row({ uniqueId: `w${i}` }));
  const afterWalls = beforeWalls.map((r) => ({ ...r, bboxMinX: r.bboxMinX! + 500, bboxMaxX: r.bboxMaxX! + 500 }));

  const beforeRoom = withParams(
    row({ uniqueId: "r1", categoryKey: "OST_Rooms", category: "Помещения", roomNumber: "45", roomName: "Кухня" }),
    { ROOM_AREA: "484" }
  );
  const afterRoom = withParams(
    row({ uniqueId: "r1", categoryKey: "OST_Rooms", category: "Помещения", roomNumber: "45", roomName: "Кухня" }),
    { ROOM_AREA: "527" }
  );

  const headline = buildDiffHeadline(diffSnapshotElements([...beforeWalls, beforeRoom], [...afterWalls, afterRoom]));

  assert.match(headline, /переставлено.*Стены — 12/);
  assert.match(headline, /площадь пом\. 45 «Кухня» выросла на \d+(\.\d+)? м²/);
});

test("a cluster of one is not worth a headline clause", () => {
  const before = [row({ uniqueId: "w1" })];
  const after = [row({ uniqueId: "w1", bboxMinX: 2000, bboxMaxX: 3000 })];

  const headline = buildDiffHeadline(diffSnapshotElements(before, after));

  assert.doesNotMatch(headline, /переставлено/);
  assert.match(headline, /изменено: 1/);
});

// --- overall counts -------------------------------------------------------

test("countChanges tallies added, removed, modified and moved separately", () => {
  const changes = diffSnapshotElements(
    [row({ uniqueId: "w1" }), row({ uniqueId: "w2" })],
    [
      row({ uniqueId: "w1", bboxMinX: 2000, bboxMaxX: 3000 }), // moved
      row({ uniqueId: "w3" }), // added; w2 removed
    ]
  );

  assert.deepEqual(countChanges(changes), { added: 1, removed: 1, modified: 1, moved: 1, total: 3 });
});
