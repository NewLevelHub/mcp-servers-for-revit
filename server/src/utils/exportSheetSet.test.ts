import test from "node:test";
import assert from "node:assert/strict";
import type { SheetInput } from "./sheetReadiness.js";
import {
  buildSheetFileName,
  DEFAULT_FILENAME_TEMPLATE,
  resolveDiscipline,
  sanitizeFileNameSegment,
  selectSheetsForExport,
  type SheetRevisionInfo,
} from "./exportSheetSet.js";

/**
 * What decides a filename and a skip, for REV-173's acceptance criteria: a
 * batch goes out named by the template, a sheet that fails readiness stays
 * home unless told otherwise, and nothing here ever touches Revit.
 */

const READY_PARAMS = [
  { name: "Разработал", displayValue: "Иванов" },
  { name: "Проверил", displayValue: "Петров" },
  { name: "ADSK_Штамп Строка 2 фамилия", displayValue: "Сидоров" },
  { name: "ADSK_Штамп Строка 6 фамилия", displayValue: "Кузнецов" },
];

let nextId = 1;
function sheet(overrides: Partial<SheetInput> = {}): SheetInput {
  const id = nextId++;
  return {
    id,
    name: "Фасады",
    number: `АР-0${id}`,
    parameters: READY_PARAMS,
    ...overrides,
  };
}

// --- filename template -------------------------------------------------------

test("the ticket's own example template fills in from sheet values", () => {
  const name = buildSheetFileName(DEFAULT_FILENAME_TEMPLATE, {
    code: "2024-15",
    discipline: "АР",
    number: "АР-01",
    name: "Фасады",
    revision: "3",
  });

  assert.equal(name, "2024-15-АР-АР-01_Фасады_3");
});

test("an empty placeholder collapses its separators instead of leaving a gap", () => {
  const name = buildSheetFileName(DEFAULT_FILENAME_TEMPLATE, {
    code: "",
    discipline: "",
    number: "АР-01",
    name: "Фасады",
    revision: "",
  });

  assert.equal(name, "АР-01_Фасады");
});

test("forbidden filesystem characters are stripped from a segment", () => {
  assert.equal(sanitizeFileNameSegment('План: этаж 1/2 "чистовой"'), "План этаж 12 чистовой");
});

test("an unknown placeholder resolves to empty rather than failing the template", () => {
  const name = buildSheetFileName("{code}-{bogus}-{number}", {
    code: "X",
    discipline: "",
    number: "01",
    name: "",
    revision: "",
  });
  assert.equal(name, "X-01");
});

test("a template that resolves to nothing at all still names the file", () => {
  assert.equal(buildSheetFileName("{bogus}", { code: "", discipline: "", number: "", name: "", revision: "" }), "лист");
});

// --- discipline -----------------------------------------------------------

test("an explicit Раздел parameter wins over the number prefix", () => {
  const discipline = resolveDiscipline("КР-01", [{ name: "Раздел", displayValue: "АР" }]);
  assert.equal(discipline, "АР");
});

test("no Раздел parameter falls back to the letters before the first separator", () => {
  assert.equal(resolveDiscipline("АР-01", []), "АР");
  assert.equal(resolveDiscipline("KR-14a", []), "KR");
});

test("a sheet number with no leading letters has no discipline guess", () => {
  assert.equal(resolveDiscipline("01", []), "");
});

// --- selection: readiness gating ----------------------------------------------

test("a sheet that fails readiness is skipped by default", () => {
  const notReady = sheet({ parameters: [] });
  const { selected, skipped } = selectSheetsForExport([notReady], [], new Map(), {});

  assert.equal(selected.length, 0);
  assert.equal(skipped.length, 1);
  assert.equal(skipped[0].reason, "not_ready");
});

test("allowNotReady lets a failing sheet through", () => {
  const notReady = sheet({ parameters: [] });
  const { selected, skipped } = selectSheetsForExport([notReady], [], new Map(), { allowNotReady: true });

  assert.equal(selected.length, 1);
  assert.equal(skipped.length, 0);
});

test("a ready sheet is exported with no skip at all", () => {
  const ready = sheet();
  const { selected, skipped } = selectSheetsForExport([ready], [], new Map(), {});

  assert.equal(selected.length, 1);
  assert.equal(skipped.length, 0);
  assert.equal(selected[0].sheetId, ready.id);
});

test("an unreadable sheet is reported, not silently dropped or graded blank", () => {
  const bad = sheet();
  const { selected, skipped } = selectSheetsForExport([], [bad], new Map(), {});

  assert.equal(selected.length, 0);
  assert.equal(skipped[0].reason, "unreadable");
});

// --- selection: по списку / по разделу / по ревизии ---------------------------

test("an explicit sheetIds list excludes everything else, по списку", () => {
  const a = sheet();
  const b = sheet();
  const { selected, skipped } = selectSheetsForExport([a, b], [], new Map(), { sheetIds: [a.id] });

  assert.deepEqual(selected.map((s) => s.sheetId), [a.id]);
  assert.equal(skipped[0].reason, "not_in_list");
});

test("a requested sheetId that names no real sheet is reported, not silently dropped", () => {
  const real = sheet();
  const { selected, skipped } = selectSheetsForExport([real], [], new Map(), {
    sheetIds: [real.id, 999999999],
  });

  assert.equal(selected.length, 1);
  const ghost = skipped.find((s) => s.sheetId === 999999999);
  assert.ok(ghost, "the nonexistent id must show up in skipped");
  assert.equal(ghost.reason, "sheet_not_found");
});

test("по разделу matches the resolved discipline, case-insensitively", () => {
  const ar = sheet({ number: "АР-01" });
  const kr = sheet({ number: "КР-01" });
  const { selected, skipped } = selectSheetsForExport([ar, kr], [], new Map(), { discipline: "ар" });

  assert.deepEqual(selected.map((s) => s.sheetId), [ar.id]);
  assert.equal(skipped[0].reason, "wrong_discipline");
});

test("по ревизии keeps only sheets carrying that revision", () => {
  const withRev = sheet();
  const withoutRev = sheet();
  const revisions = new Map<number, SheetRevisionInfo>([
    [withRev.id, { sheetId: withRev.id, revisions: [{ sequenceNumber: 2, description: "Выдача 2" }] }],
  ]);

  const { selected, skipped } = selectSheetsForExport([withRev, withoutRev], [], revisions, {
    revisionDescription: "Выдача 2",
  });

  assert.deepEqual(selected.map((s) => s.sheetId), [withRev.id]);
  assert.equal(skipped[0].reason, "no_such_revision");
});

test("{revision} in the template pulls the latest revision on that sheet, no filter needed", () => {
  const withRevs = sheet();
  const revisions = new Map<number, SheetRevisionInfo>([
    [
      withRevs.id,
      {
        sheetId: withRevs.id,
        revisions: [
          { sequenceNumber: 1, description: "Выдача 1" },
          { sequenceNumber: 2, description: "Выдача 2" },
        ],
      },
    ],
  ]);

  const { selected } = selectSheetsForExport([withRevs], [], revisions, {
    fileNameTemplate: "{number}_{revision}",
  });

  assert.match(selected[0].fileName, /_2$/);
});

test("skip reasons are checked in a sensible order: list, then раздел, then readiness", () => {
  const outOfList = sheet({ number: "КР-01" });
  const included = sheet({ number: "АР-01" });
  const { skipped } = selectSheetsForExport([outOfList, included], [], new Map(), {
    sheetIds: [included.id],
    discipline: "АР",
  });

  // outOfList would also fail the раздел filter, but "not_in_list" is the more useful reason.
  assert.equal(skipped[0].reason, "not_in_list");
});
