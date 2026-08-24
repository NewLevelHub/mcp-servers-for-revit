import test from "node:test";
import assert from "node:assert/strict";
import { findNumberingGaps } from "./sheetIndex.js";

/**
 * What counts as a hole in a drawing set's numbering (REV-174) — the other
 * half of "дубли и пропуски попадают в отчёт", duplicates already covered by
 * `findDuplicateNumbers` in sheetReadiness.test.ts.
 */

test("a plain numeric run reports the number it skipped", () => {
  const gaps = findNumberingGaps(["2", "3", "4", "6"]);
  assert.deepEqual(gaps, [{ prefix: "", missing: ["5"] }]);
});

test("a run with no gap reports nothing", () => {
  assert.deepEqual(findNumberingGaps(["2", "3", "4"]), []);
});

test("a раздел prefix is its own sequence, zero-padded to match its neighbours", () => {
  const gaps = findNumberingGaps(["АР-01", "АР-02", "АР-04"]);
  assert.deepEqual(gaps, [{ prefix: "АР-", missing: ["03"] }]);
});

test("different prefixes never leak into each other's sequence", () => {
  const gaps = findNumberingGaps(["АР-01", "АР-02", "КР-01", "КР-03"]);
  assert.deepEqual(gaps, [
    { prefix: "КР-", missing: ["02"] },
  ]);
});

test("a number with no trailing digits at all is not sequenced — nothing to compare it to", () => {
  assert.deepEqual(findNumberingGaps(["Титул", "Начальный вид"]), []);
});

test("a lone member of a group is not a gap by itself", () => {
  assert.deepEqual(findNumberingGaps(["TEST-REV22"]), []);
});

test("the missing number is padded to the group's own width", () => {
  const gaps = findNumberingGaps(["05", "07"]);
  assert.deepEqual(gaps, [{ prefix: "", missing: ["06"] }]);
});

test("more than one gap in the same sequence are all reported, in order", () => {
  const gaps = findNumberingGaps(["1", "3", "5", "8"]);
  assert.deepEqual(gaps, [{ prefix: "", missing: ["2", "4", "6", "7"] }]);
});

test("blank entries are ignored rather than crashing the scan", () => {
  assert.deepEqual(findNumberingGaps(["1", "", "  ", "3"]), [{ prefix: "", missing: ["2"] }]);
});

test("a real heterogeneous ведомость only flags the sequence that actually has a hole", () => {
  // The set this ticket was verified against: "000"/"00" are title sheets, "2".."39"
  // are the plans, and both happen to share the empty prefix. Width tells them apart.
  const numbers = ["000", "00", "1.1", "1.2", "1.3", "2", "3", "5", "TEST-REV22"];
  const gaps = findNumberingGaps(numbers);
  // "2","3","5" (width 1) share a real gap at 4; "1.1"/"1.2"/"1.3" share prefix "1."
  // with no gap; "000"(width 3), "00"(width 2) and "TEST-REV22" are lone members of
  // their own (prefix, width) groups and must NOT be treated as one sequence with "2".
  assert.deepEqual(gaps, [{ prefix: "", missing: ["4"] }]);
});

test("numbers sharing a prefix but not a padding width are not treated as one sequence", () => {
  // "000"/"00" are title sheets, not holes either side of "2" — different width, different run.
  assert.deepEqual(findNumberingGaps(["00", "2", "3"]), []);
});
