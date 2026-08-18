import test from "node:test";
import assert from "node:assert/strict";
import {
  capLists,
  filterListsByName,
  paginateRows,
  projectRowFields,
  wantsAllFields,
} from "./responseTrimming.js";

const room = (n: number) => ({
  id: n,
  uniqueId: `uid-${n}`,
  name: `Комната ${n}`,
  number: String(n),
  level: "1 этаж",
  area: 12.5,
  volume: 33.75,
  clearHeight: 2700,
  comments: "",
});

const rooms = (count: number) => Array.from({ length: count }, (_, i) => room(i + 1));

// --- wantsAllFields ---------------------------------------------------------

test("wantsAllFields spots 'all' in any casing, and nothing else", () => {
  assert.equal(wantsAllFields(["all"]), true);
  assert.equal(wantsAllFields(["name", "ALL"]), true);
  assert.equal(wantsAllFields([" All "]), true);
  assert.equal(wantsAllFields(["name", "area"]), false);
  assert.equal(wantsAllFields([]), false);
  assert.equal(wantsAllFields(undefined), false);
});

// --- projectRowFields -------------------------------------------------------

test("projection keeps only the named fields", () => {
  const [first] = projectRowFields([room(1)], ["name", "area"]) as Array<
    Record<string, unknown>
  >;
  assert.deepEqual(Object.keys(first).sort(), ["area", "name"]);
});

test("an unknown field name is skipped rather than added as undefined", () => {
  const [first] = projectRowFields([room(1)], ["name", "нетТакогоПоля"]) as Array<
    Record<string, unknown>
  >;
  assert.deepEqual(Object.keys(first), ["name"]);
  assert.ok(!("нетТакогоПоля" in first));
});

test("a row matching none of the fields is returned whole", () => {
  // Dropping every key would hand the model an empty object and no way to tell
  // an empty room from a bad field list.
  const [first] = projectRowFields([{ other: 1 }], ["name"]) as Array<
    Record<string, unknown>
  >;
  assert.deepEqual(first, { other: 1 });
});

test("an empty field list projects nothing away", () => {
  assert.deepEqual(projectRowFields([room(1)], []), [room(1)]);
});

test("non-object rows survive projection untouched", () => {
  assert.deepEqual(projectRowFields([1, "two", null], ["name"]), [1, "two", null]);
});

// --- paginateRows -----------------------------------------------------------

test("paging reports total, offset, limit and where to continue", () => {
  const result = paginateRows(
    { success: true, totalRooms: 572, rooms: rooms(572) },
    { key: "rooms", limit: 300, fields: ["all"] }
  ) as Record<string, any>;

  assert.equal(result.rooms.length, 300);
  assert.equal(result.roomsPagination.total, 572);
  assert.equal(result.roomsPagination.returned, 300);
  assert.equal(result.roomsPagination.hasMore, true);
  assert.equal(result.roomsPagination.nextOffset, 300);
});

test("the last page reports hasMore false and no nextOffset", () => {
  const result = paginateRows({ rooms: rooms(572) }, {
    key: "rooms",
    offset: 300,
    limit: 300,
    fields: ["all"],
  }) as Record<string, any>;

  assert.equal(result.rooms.length, 272);
  assert.equal(result.roomsPagination.hasMore, false);
  assert.equal(result.roomsPagination.nextOffset, undefined);
});

test("counters outside the paged array are left alone", () => {
  // totalRooms/totalArea are what answers "сколько помещений" — paging must not
  // rewrite them to the page size.
  const result = paginateRows(
    { success: true, totalRooms: 572, totalArea: 7000, rooms: rooms(572) },
    { key: "rooms", limit: 10 }
  ) as Record<string, any>;

  assert.equal(result.totalRooms, 572);
  assert.equal(result.totalArea, 7000);
  assert.equal(result.success, true);
});

test("projection and paging combine, and the note says how to get the rest", () => {
  const result = paginateRows({ rooms: rooms(5) }, {
    key: "rooms",
    limit: 2,
    fields: ["name", "area"],
  }) as Record<string, any>;

  assert.deepEqual(Object.keys(result.rooms[0]).sort(), ["area", "name"]);
  assert.deepEqual(result.roomsPagination.fields, ["name", "area"]);
  assert.match(result.roomsPagination.note, /fields:\["all"\]/);
});

test("fields:['all'] adds no projection note", () => {
  const result = paginateRows({ rooms: rooms(2) }, {
    key: "rooms",
    fields: ["all"],
  }) as Record<string, any>;

  assert.equal(result.roomsPagination.fields, undefined);
  assert.equal(result.roomsPagination.note, undefined);
  assert.deepEqual(result.rooms, rooms(2));
});

test("an offset past the end returns an empty page, not an error", () => {
  const result = paginateRows({ rooms: rooms(3) }, {
    key: "rooms",
    offset: 99,
    limit: 10,
  }) as Record<string, any>;

  assert.deepEqual(result.rooms, []);
  assert.equal(result.roomsPagination.total, 3);
  assert.equal(result.roomsPagination.hasMore, false);
});

test("an empty array pages cleanly", () => {
  const result = paginateRows({ rooms: [] }, { key: "rooms", limit: 10 }) as Record<
    string,
    any
  >;
  assert.deepEqual(result.rooms, []);
  assert.equal(result.roomsPagination.total, 0);
  assert.equal(result.roomsPagination.hasMore, false);
});

test("negative offset and zero limit are clamped instead of returning nothing", () => {
  const result = paginateRows({ rooms: rooms(5) }, {
    key: "rooms",
    offset: -10,
    limit: 0,
  }) as Record<string, any>;

  assert.equal(result.roomsPagination.offset, 0);
  assert.equal(result.rooms.length, 1);
});

test("a response without the key, or not an object, is returned untouched", () => {
  const noKey = { success: true };
  assert.equal(paginateRows(noKey, { key: "rooms" }), noKey);
  assert.equal(paginateRows("plain text", { key: "rooms" }), "plain text");
  assert.equal(paginateRows(null, { key: "rooms" }), null);
  const array: unknown[] = [1, 2];
  assert.equal(paginateRows(array, { key: "rooms" }), array);
});

// --- capLists ---------------------------------------------------------------

test("every long list is capped and its real length recorded", () => {
  const result = capLists(
    {
      success: true,
      fillPatterns: Array.from({ length: 400 }, (_, i) => ({ id: i })),
      lineStyles: Array.from({ length: 12 }, (_, i) => ({ id: i })),
    },
    { limit: 50 }
  ) as Record<string, any>;

  assert.equal(result.fillPatterns.length, 50);
  assert.equal(result.lineStyles.length, 12, "a short list must not be touched");
  assert.deepEqual(result.listsTruncated.totals, { fillPatterns: 400 });
  assert.equal(result.listsTruncated.limit, 50);
});

test("a kept list is exempt however long it is", () => {
  const result = capLists(
    { titleBlocks: Array.from({ length: 200 }, (_, i) => i) },
    { limit: 5, keep: ["titleBlocks"] }
  ) as Record<string, any>;

  assert.equal(result.titleBlocks.length, 200);
  assert.equal(result.listsTruncated, undefined);
});

test("nothing over the limit means no truncation marker at all", () => {
  const input = { a: [1, 2], b: "text", success: true };
  const result = capLists(input, { limit: 10 }) as Record<string, any>;

  assert.equal(result.listsTruncated, undefined);
  assert.deepEqual(result, input);
});

test("capLists leaves non-objects alone", () => {
  assert.equal(capLists(null, { limit: 5 }), null);
  assert.equal(capLists("text", { limit: 5 }), "text");
});

test("the truncation note carries the caller's advice on how to narrow", () => {
  const result = capLists({ items: [1, 2, 3] }, {
    limit: 1,
    narrowHint: "повтори вызов с nameFilter.",
  }) as Record<string, any>;

  assert.match(result.listsTruncated.note, /nameFilter/);
  assert.match(result.listsTruncated.note, /не выдумывай/);
});

// --- filterListsByName ------------------------------------------------------

const styles = () => ({
  success: true,
  lineStyles: [
    { id: 1, name: "ADSK_Тонкая" },
    { id: 2, name: "Осевая" },
    { id: 3, name: "adsk_Толстая" },
  ],
  fillPatterns: [
    { id: 4, name: "Бетон" },
    { id: 5, name: "ADSK Кирпич" },
  ],
});

test("filtering by name spans every list and ignores case", () => {
  const result = filterListsByName(styles(), "adsk") as Record<string, any>;

  assert.deepEqual(
    result.lineStyles.map((s: any) => s.id),
    [1, 3]
  );
  assert.deepEqual(
    result.fillPatterns.map((s: any) => s.id),
    [5]
  );
  assert.equal(result.nameFilter, "adsk");
});

test("filtering keeps non-array fields, so success still reads correctly", () => {
  const result = filterListsByName(styles(), "нетТакого") as Record<string, any>;

  assert.equal(result.success, true);
  assert.deepEqual(result.lineStyles, []);
  assert.deepEqual(result.fillPatterns, []);
});

test("an entry without a name cannot match and is dropped", () => {
  const result = filterListsByName(
    { items: [{ id: 1 }, { id: 2, name: "Бетон" }, "loose string"] },
    "бетон"
  ) as Record<string, any>;

  assert.deepEqual(result.items, [{ id: 2, name: "Бетон" }]);
});

test("a blank filter is a no-op rather than an empty result", () => {
  const input = styles();
  assert.equal(filterListsByName(input, "   "), input);
  assert.equal(filterListsByName(input, ""), input);
});

test("filtering then capping keeps the matches — the cap must not undo the search", () => {
  const many = {
    fillPatterns: [
      ...Array.from({ length: 300 }, (_, i) => ({ id: i, name: `Штриховка ${i}` })),
      { id: 999, name: "ADSK Кирпич" },
    ],
  };

  const found = capLists(filterListsByName(many, "ADSK"), { limit: 60 }) as Record<
    string,
    any
  >;

  assert.deepEqual(found.fillPatterns, [{ id: 999, name: "ADSK Кирпич" }]);
  assert.equal(found.listsTruncated, undefined);
});

test("capLists does not disturb the success flag the refusal check reads", () => {
  const result = capLists(
    { success: false, message: "нет доступа", items: [1, 2, 3] },
    { limit: 1 }
  ) as Record<string, any>;

  assert.equal(result.success, false);
  assert.equal(result.message, "нет доступа");
});
