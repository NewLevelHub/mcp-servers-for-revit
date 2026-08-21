import test from "node:test";
import assert from "node:assert/strict";
import {
  canonicalParameters,
  defaultSnapshotLabel,
  hashParameters,
  normalizeParameterValue,
  toSnapshotRow,
  toSnapshotRows,
  type RawSnapshotElement,
} from "./modelSnapshot.js";

/**
 * The rule that decides whether an element counts as changed (REV-170).
 *
 * Everything a snapshot promises rests on it: a hash that moves when nothing
 * moved fills the next diff with elements nobody touched, and a hash that stays
 * put when a value changed hides the edit the architect asked about. Both
 * failures are invisible in Revit, which is why they are pinned here.
 */

// --- the two properties the ticket names ------------------------------------

test("a changed value changes the hash", () => {
  const before = hashParameters({ ALL_MODEL_MARK: "Ст-1" });
  const after = hashParameters({ ALL_MODEL_MARK: "Ст-2" });

  assert.notEqual(before, after);
});

test("the order the parameters arrive in does not change the hash", () => {
  const forwards = hashParameters({
    ALL_MODEL_MARK: "Ст-1",
    WALL_USER_HEIGHT_PARAM: 9.84,
    PHASE_CREATED: "Новая конструкция",
  });

  const backwards = hashParameters({
    PHASE_CREATED: "Новая конструкция",
    WALL_USER_HEIGHT_PARAM: 9.84,
    ALL_MODEL_MARK: "Ст-1",
  });

  assert.equal(forwards, backwards);
});

// --- what "changed" has to mean ---------------------------------------------

test("a numeric change smaller than the precision is not a change", () => {
  // What a regeneration leaves behind: the same wall, re-derived.
  const before = hashParameters({ WALL_USER_HEIGHT_PARAM: 9.84 });
  const after = hashParameters({ WALL_USER_HEIGHT_PARAM: 9.8400000001 });

  assert.equal(before, after);
});

test("a numeric change above the precision is a change", () => {
  const before = hashParameters({ WALL_USER_HEIGHT_PARAM: 9.84 });
  const after = hashParameters({ WALL_USER_HEIGHT_PARAM: 9.8401 });

  assert.notEqual(before, after);
});

test("clearing a parameter changes the hash", () => {
  const filled = hashParameters({ ALL_MODEL_MARK: "Ст-1" });
  const cleared = hashParameters({ ALL_MODEL_MARK: "" });

  assert.notEqual(filled, cleared);
});

test("a blank parameter hashes the same as a missing one", () => {
  // The plugin omits a parameter it read as null, so the two shapes reach the
  // server for the same element depending on nothing the architect did.
  const blank = hashParameters({ ALL_MODEL_MARK: "Ст-1", ALL_MODEL_INSTANCE_COMMENTS: "" });
  const missing = hashParameters({ ALL_MODEL_MARK: "Ст-1" });
  const nulled = hashParameters({ ALL_MODEL_MARK: "Ст-1", ALL_MODEL_INSTANCE_COMMENTS: null });

  assert.equal(blank, missing);
  assert.equal(nulled, missing);
});

test("adding a parameter changes the hash", () => {
  const bare = hashParameters({ ALL_MODEL_MARK: "Ст-1" });
  const marked = hashParameters({ ALL_MODEL_MARK: "Ст-1", ALL_MODEL_INSTANCE_COMMENTS: "снести" });

  assert.notEqual(bare, marked);
});

test("a value moved from one parameter to another changes the hash", () => {
  // The delimiters exist for exactly this: concatenating names and values
  // without them makes these two indistinguishable.
  const asMark = hashParameters({ ALL_MODEL_MARK: "AB", ALL_MODEL_INSTANCE_COMMENTS: "C" });
  const asComment = hashParameters({ ALL_MODEL_MARK: "A", ALL_MODEL_INSTANCE_COMMENTS: "BC" });

  assert.notEqual(asMark, asComment);
});

test("the hash of no parameters at all is stable", () => {
  assert.equal(hashParameters({}), hashParameters(undefined));
});

// --- normalisation ----------------------------------------------------------

test("-0 and 0 normalise the same", () => {
  assert.equal(normalizeParameterValue(-0), normalizeParameterValue(0));
});

test("surrounding whitespace is not a change, inner whitespace is", () => {
  assert.equal(normalizeParameterValue(" Ст-1 "), "Ст-1");
  assert.notEqual(normalizeParameterValue("Ст-1"), normalizeParameterValue("Ст - 1"));
});

test("a yes/no parameter normalises to a digit either way it arrives", () => {
  assert.equal(normalizeParameterValue(true), "1");
  assert.equal(normalizeParameterValue(false), "0");
});

test("canonical parameters are sorted and stripped of blanks", () => {
  const canonical = canonicalParameters({
    WALL_TOP_OFFSET: 0,
    ALL_MODEL_MARK: "Ст-1",
    ALL_MODEL_INSTANCE_COMMENTS: "   ",
  });

  assert.deepEqual(Object.keys(canonical), ["ALL_MODEL_MARK", "WALL_TOP_OFFSET"]);
  assert.equal(canonical.WALL_TOP_OFFSET, "0");
});

// --- rows -------------------------------------------------------------------

function wall(overrides: Partial<RawSnapshotElement> = {}): RawSnapshotElement {
  return {
    elementId: 667305,
    uniqueId: "aaaa-bbbb-1",
    categoryKey: "OST_Walls",
    category: "Стены",
    familyName: "Базовая стена",
    typeName: "Бетон 500",
    typeId: 169118,
    levelName: "2 этаж",
    boundingBoxMm: {
      min: { x: 0, y: 0, z: 3900 },
      max: { x: 6000.04, y: 500, z: 7000 },
    },
    parameters: { ALL_MODEL_MARK: "Ст-1" },
    ...overrides,
  };
}

test("a row carries the stable category key and the localised name separately", () => {
  const row = toSnapshotRow(wall());

  assert.equal(row.categoryKey, "OST_Walls");
  assert.equal(row.category, "Стены");
});

test("the same element read in Russian and in English has the same hash", () => {
  // The test machine's Revit changes UI language between sessions. Only the
  // display names move; nothing a comparison keys on may.
  const russian = toSnapshotRow(wall({ category: "Стены" }));
  const english = toSnapshotRow(wall({ category: "Walls" }));

  assert.equal(russian.paramHash, english.paramHash);
  assert.equal(russian.categoryKey, english.categoryKey);
});

test("a bounding box is rounded to a tenth of a millimetre", () => {
  const row = toSnapshotRow(wall());

  assert.equal(row.bboxMaxX, 6000);
});

test("an element with no geometry stores nulls rather than zeros", () => {
  const row = toSnapshotRow(wall({ boundingBoxMm: null }));

  assert.equal(row.bboxMinX, null);
  assert.equal(row.bboxMaxZ, null);
  // A zero would place the element at the origin, and the diff would report it
  // as having been moved there.
  assert.notEqual(row.bboxMinX, 0);
});

test("rows drop elements with no UniqueId", () => {
  const rows = toSnapshotRows([wall(), wall({ uniqueId: "" }), { ...wall(), uniqueId: undefined as never }]);

  assert.equal(rows.length, 1);
});

test("a UniqueId repeated inside a page produces one row", () => {
  const rows = toSnapshotRows([wall(), wall({ elementId: 667306 })]);

  assert.equal(rows.length, 1);
  // The last reading wins, which matches what the INSERT OR REPLACE does.
  assert.equal(rows[0].elementId, 667306);
});

test("params are stored exactly as they were hashed", () => {
  const row = toSnapshotRow(
    wall({
      parameters: {
        WALL_TOP_OFFSET: 0.0000001,
        ALL_MODEL_MARK: " Ст-1 ",
        ALL_MODEL_INSTANCE_COMMENTS: "",
      },
    })
  );

  // A zero offset is a value, not a blank — it rounds to "0" and stays. What the
  // architect never filled in is what drops out.
  assert.deepEqual(JSON.parse(row.paramsJson), {
    ALL_MODEL_MARK: "Ст-1",
    WALL_TOP_OFFSET: "0",
  });
  assert.equal(
    row.paramHash,
    hashParameters({ ALL_MODEL_MARK: "Ст-1", WALL_TOP_OFFSET: 0 })
  );
});

// --- labels -----------------------------------------------------------------

test("the default label reads as a date an architect would write", () => {
  const label = defaultSnapshotLabel(new Date(2026, 7, 19, 9, 5));

  assert.equal(label, "снимок 19.08.2026 09:05");
});
