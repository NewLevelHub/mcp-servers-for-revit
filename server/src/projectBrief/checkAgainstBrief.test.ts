import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  checkRoomAreaMin,
  checkRoomCount,
  groupRoomsByName,
  type ModelRoom,
} from "./checkAgainstBrief.js";

const SOURCE = { document: "ТЗ", clause: "", quote: "Студия — 25 шт." };

describe("groupRoomsByName", () => {
  it("groups rooms whose names differ only by case/whitespace", () => {
    const rooms: ModelRoom[] = [
      { name: "Студия", area: 30 },
      { name: "  студия  ", area: 32 },
      { name: "СТУДИЯ", area: 28 },
    ];
    const groups = groupRoomsByName(rooms);
    assert.equal(groups.length, 1);
    assert.equal(groups[0].rooms.length, 3);
  });

  it("a room with no name is skipped rather than forming a blank group", () => {
    const groups = groupRoomsByName([{ name: "" }, { name: "Кладовая" }]);
    assert.equal(groups.length, 1);
  });
});

describe("checkRoomCount — ticket's own worked example: «требуется 25 студий — в модели 21»", () => {
  it("reports the exact discrepancy shape from the ticket", () => {
    const rooms: ModelRoom[] = Array.from({ length: 21 }, () => ({ name: "Студия", area: 30 }));
    const result = checkRoomCount("студия", 25, SOURCE, groupRoomsByName(rooms));
    assert.equal(result.matched, true);
    assert.equal(result.required, 25);
    assert.equal(result.actual, 21);
    assert.equal(result.ok, false);
    assert.match(result.message, /требуется 25.*модели 21/);
  });

  it("a matching count is ok:true", () => {
    const rooms: ModelRoom[] = Array.from({ length: 25 }, () => ({ name: "Студия" }));
    const result = checkRoomCount("студия", 25, SOURCE, groupRoomsByName(rooms));
    assert.equal(result.ok, true);
  });

  it("room names carrying a suffix («Студия 205») still count toward the object", () => {
    const rooms: ModelRoom[] = [{ name: "Студия 101" }, { name: "Студия-102" }, { name: "Студия" }];
    const result = checkRoomCount("студия", 3, SOURCE, groupRoomsByName(rooms));
    assert.equal(result.matched, true);
    assert.equal(result.actual, 3);
  });

  it("an unrelated room name («Студия-бар») is NOT folded into «студия» — no separator match", () => {
    const rooms: ModelRoom[] = [{ name: "Студийная" }];
    const result = checkRoomCount("студия", 1, SOURCE, groupRoomsByName(rooms));
    assert.equal(result.matched, false);
  });

  it(
    "an apartment-type object with no matching room name reports matched:false, not a silent 0 " +
      "(known limitation: an apartment is several Room elements, not one)",
    () => {
      const rooms: ModelRoom[] = [{ name: "Кухня" }, { name: "Спальня" }];
      const result = checkRoomCount("2-комнатная квартира", 40, SOURCE, groupRoomsByName(rooms));
      assert.equal(result.matched, false);
      assert.equal(result.ok, false);
      assert.match(result.message, /не найдено/);
    }
  );
});

describe("checkRoomAreaMin", () => {
  it("names the specific rooms under the minimum, not just a count", () => {
    const rooms: ModelRoom[] = [
      { name: "Кладовая 1", area: 2.5 },
      { name: "Кладовая 2", area: 4 },
      { name: "Кладовая 3", area: 1.8 },
    ];
    const result = checkRoomAreaMin("кладовая", 3, SOURCE, groupRoomsByName(rooms));
    assert.equal(result.matched, true);
    assert.equal(result.checkedCount, 3);
    assert.equal(result.violatingCount, 2);
    assert.deepEqual(
      result.violations.map((v) => v.name).sort(),
      ["Кладовая 1", "Кладовая 3"]
    );
    assert.equal(result.ok, false);
  });

  it("all rooms meeting the minimum is ok:true with no violations", () => {
    const rooms: ModelRoom[] = [{ name: "Офис", area: 15 }, { name: "Офис", area: 20 }];
    const result = checkRoomAreaMin("офис", 12, SOURCE, groupRoomsByName(rooms));
    assert.equal(result.ok, true);
    assert.equal(result.violatingCount, 0);
  });

  it("a room with no area recorded is neither a violation nor silently passed", () => {
    const rooms: ModelRoom[] = [{ name: "Офис" }];
    const result = checkRoomAreaMin("офис", 12, SOURCE, groupRoomsByName(rooms));
    assert.equal(result.checkedCount, 1);
    assert.equal(result.violatingCount, 0);
  });
});
