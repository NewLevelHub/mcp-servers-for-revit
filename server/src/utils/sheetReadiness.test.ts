import test from "node:test";
import assert from "node:assert/strict";
import {
  REQUIRED_SHEET_FIELDS,
  buildReadinessReport,
  findDuplicateNumbers,
  summarizeReadiness,
  type SheetInput,
} from "./sheetReadiness.js";

/** A sheet whose штамп has every required line, filled. */
function goodSheet(id: number, number: string, name = "План 1 этажа"): SheetInput {
  return {
    id,
    number,
    name,
    parameters: [
      { name: "Номер листа", displayValue: number },
      { name: "ADSK_Штамп Строка 4 фамилия", displayValue: "Иванов" },
      { name: "ADSK_Штамп Строка 5 фамилия", displayValue: "Петров" },
      { name: "ADSK_Штамп Строка 2 фамилия", displayValue: "Сидоров" },
      { name: "ADSK_Штамп Строка 6 фамилия", displayValue: "Кузнецов" },
    ],
  };
}

function withBlank(sheet: SheetInput, parameterName: string): SheetInput {
  return {
    ...sheet,
    parameters: sheet.parameters.map((parameter) =>
      parameter.name === parameterName ? { ...parameter, displayValue: "" } : parameter
    ),
  };
}

function without(sheet: SheetInput, parameterName: string): SheetInput {
  return {
    ...sheet,
    parameters: sheet.parameters.filter((parameter) => parameter.name !== parameterName),
  };
}

// --- duplicates -------------------------------------------------------------

test("a number on two sheets is a duplicate; a number on one is not", () => {
  const sheets = [goodSheet(1, "АР-01"), goodSheet(2, "АР-02"), goodSheet(3, "АР-01")];
  assert.deepEqual(findDuplicateNumbers(sheets), ["АР-01"]);
});

test("blank numbers are not duplicates of each other", () => {
  // Two unnumbered sheets are two separate omissions, not a numbering clash.
  const sheets = [goodSheet(1, ""), goodSheet(2, "")];
  assert.deepEqual(findDuplicateNumbers(sheets), []);
});

test("duplicates come back in natural sheet order", () => {
  const sheets = [
    goodSheet(1, "АР-10"),
    goodSheet(2, "АР-10"),
    goodSheet(3, "АР-2"),
    goodSheet(4, "АР-2"),
  ];
  assert.deepEqual(findDuplicateNumbers(sheets), ["АР-2", "АР-10"]);
});

// --- per-sheet grading ------------------------------------------------------

test("a fully filled sheet is ready and reports nothing", () => {
  const report = buildReadinessReport([goodSheet(1, "АР-01")]);
  assert.equal(report.sheets[0].ready, true);
  assert.deepEqual(report.sheets[0].issues, []);
  assert.equal(report.summary.readySheets, 1);
  assert.equal(report.summary.sheetsWithIssues, 0);
});

test("a blank штамп line is reported as empty_field with a human label", () => {
  const report = buildReadinessReport([
    withBlank(goodSheet(1, "АР-01"), "ADSK_Штамп Строка 6 фамилия"),
  ]);

  const issue = report.sheets[0].issues.find((i) => i.kind === "empty_field");
  assert.equal(issue?.field, "normControl");
  assert.match(issue!.detail, /Н\. контроль/);
  assert.equal(report.sheets[0].ready, false);
});

test("a штамп without the line at all is field_absent, not empty_field", () => {
  // Different diagnosis, different fix: this needs another title block, not a name
  // typed in. Telling the architect to "fill in Н.контроль" here wastes their time.
  const report = buildReadinessReport([
    without(goodSheet(1, "АР-01"), "ADSK_Штамп Строка 6 фамилия"),
  ]);

  const kinds = report.sheets[0].issues.map((i) => i.kind);
  assert.ok(kinds.includes("field_absent"));
  assert.ok(!kinds.includes("empty_field"));
  assert.match(
    report.sheets[0].issues[0].detail,
    /другой шаблон/,
    "the message must point at the template, not at the person"
  );
});

test("an absent field is not counted as blank in the summary", () => {
  const report = buildReadinessReport([
    without(goodSheet(1, "АР-01"), "ADSK_Штамп Строка 6 фамилия"),
  ]);
  assert.deepEqual(report.summary.blankFieldCounts, []);
});

test("a missing sheet number is reported", () => {
  const report = buildReadinessReport([goodSheet(1, "")]);
  assert.ok(report.sheets[0].issues.some((i) => i.kind === "missing_number"));
});

test("a duplicate number is reported on every sheet that carries it", () => {
  const report = buildReadinessReport([goodSheet(1, "АР-01"), goodSheet(2, "АР-01")]);
  for (const sheet of report.sheets) {
    assert.ok(sheet.issues.some((i) => i.kind === "duplicate_number"), `sheet ${sheet.id}`);
  }
  assert.deepEqual(report.summary.duplicateNumbers, ["АР-01"]);
});

test("a sheet cannot be both missing and duplicated on its number", () => {
  const report = buildReadinessReport([goodSheet(1, ""), goodSheet(2, "АР-01")]);
  const kinds = report.sheets[0].issues.map((i) => i.kind);
  assert.ok(kinds.includes("missing_number"));
  assert.ok(!kinds.includes("duplicate_number"));
});

test("a blank sheet name is reported", () => {
  const report = buildReadinessReport([goodSheet(1, "АР-01", "   ")]);
  assert.ok(report.sheets[0].issues.some((i) => i.kind === "missing_name"));
});

test("whitespace does not count as a filled field", () => {
  const sheet = goodSheet(1, "АР-01");
  sheet.parameters = sheet.parameters.map((p) =>
    p.name === "ADSK_Штамп Строка 4 фамилия" ? { ...p, displayValue: "   " } : p
  );
  const report = buildReadinessReport([sheet]);
  assert.ok(
    report.sheets[0].issues.some((i) => i.kind === "empty_field" && i.field === "drawnBy")
  );
});

test("a parameter missing displayValue entirely reads as blank", () => {
  const sheet = goodSheet(1, "АР-01");
  sheet.parameters = sheet.parameters.map((p) =>
    p.name === "ADSK_Штамп Строка 5 фамилия" ? { name: p.name } : p
  );
  const report = buildReadinessReport([sheet]);
  assert.ok(
    report.sheets[0].issues.some((i) => i.kind === "empty_field" && i.field === "checkedBy")
  );
});

test("aliases are matched case-insensitively", () => {
  const sheet: SheetInput = {
    id: 1,
    number: "АР-01",
    name: "План",
    parameters: [
      { name: "разработал", displayValue: "Иванов" },
      { name: "ПРОВЕРИЛ", displayValue: "Петров" },
      { name: "гип", displayValue: "Сидоров" },
      { name: "Нормоконтроль", displayValue: "Кузнецов" },
    ],
  };
  assert.equal(buildReadinessReport([sheet]).sheets[0].ready, true);
});

test("only the requested fields are checked", () => {
  const report = buildReadinessReport(
    [withBlank(goodSheet(1, "АР-01"), "ADSK_Штамп Строка 6 фамилия")],
    ["drawnBy"]
  );
  assert.equal(report.sheets[0].ready, true);
});

test("an unknown field name is skipped rather than failing every sheet", () => {
  const report = buildReadinessReport([goodSheet(1, "АР-01")], ["нетТакогоПоля"]);
  assert.equal(report.sheets[0].ready, true);
});

// --- summary ----------------------------------------------------------------

test("blank field counts are ordered worst first", () => {
  const sheets = [
    withBlank(goodSheet(1, "АР-01"), "ADSK_Штамп Строка 6 фамилия"),
    withBlank(goodSheet(2, "АР-02"), "ADSK_Штамп Строка 6 фамилия"),
    withBlank(goodSheet(3, "АР-03"), "ADSK_Штамп Строка 4 фамилия"),
  ];
  const { blankFieldCounts } = buildReadinessReport(sheets).summary;

  assert.deepEqual(
    blankFieldCounts.map((f) => [f.field, f.sheets]),
    [
      ["normControl", 2],
      ["drawnBy", 1],
    ]
  );
});

test("a clean set is summarised as ready", () => {
  const report = buildReadinessReport([goodSheet(1, "АР-01"), goodSheet(2, "АР-02")]);
  assert.match(summarizeReadiness(report.summary), /к выдаче готовы/);
});

test("the summary names the duplicates and the worst blank field", () => {
  const sheets = [
    withBlank(goodSheet(1, "АР-01"), "ADSK_Штамп Строка 6 фамилия"),
    withBlank(goodSheet(2, "АР-01"), "ADSK_Штамп Строка 6 фамилия"),
  ];
  const text = summarizeReadiness(buildReadinessReport(sheets).summary);

  assert.match(text, /АР-01/);
  assert.match(text, /Н\. контроль/);
  assert.match(text, /2 из 2/);
});

test("no sheets at all says so instead of claiming everything is ready", () => {
  const report = buildReadinessReport([]);
  assert.equal(summarizeReadiness(report.summary), "В проекте нет листов.");
});

test("the default field list is the four штамп signatures", () => {
  assert.deepEqual(
    [...REQUIRED_SHEET_FIELDS],
    ["drawnBy", "checkedBy", "chiefEngineer", "normControl"]
  );
});
