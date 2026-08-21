import test from "node:test";
import assert from "node:assert/strict";
import {
  buildSiteMessage,
  compareGrids,
  compareLevels,
  comparePlacement,
  comparePoints,
  gridOffsetMm,
} from "./siteComparison.js";

const level = (name: string, elevationMm: number) => ({ name, elevationMm });

const grid = (name: string, x: number) => ({
  name,
  startMm: { x, y: -10000, z: 0 },
  endMm: { x, y: 10000, z: 0 },
});

// --- levels -----------------------------------------------------------------

test("a 50 mm difference is reported with both numbers", () => {
  // The acceptance case of REV-169: the architect must see ours, theirs and the gap
  // without opening either model.
  const findings = compareLevels([level("2 этаж", 3900)], [level("2 этаж", 3850)]);

  assert.equal(findings.length, 1);
  assert.equal(findings[0].kind, "mismatch");
  assert.equal(findings[0].differenceMm, 50);
  assert.match(findings[0].text, /3900/);
  assert.match(findings[0].text, /3850/);
  assert.match(findings[0].text, /50/);
});

test("levels that agree produce nothing at all", () => {
  const findings = compareLevels(
    [level("1 этаж", 0), level("2 этаж", 3900)],
    [level("1 этаж", 0), level("2 этаж", 3900)]
  );

  assert.deepEqual(findings, []);
});

test("float noise from feet and back is not a finding", () => {
  // Revit stores elevations in feet; 3900 mm comes back as 3899.9999999999995.
  const findings = compareLevels([level("2 этаж", 3900)], [level("2 этаж", 3899.9999999999995)]);

  assert.deepEqual(findings, []);
});

test("a level missing from the link is named", () => {
  const findings = compareLevels(
    [level("1 этаж", 0), level("Кровля", 12000)],
    [level("1 этаж", 0)]
  );

  assert.equal(findings.length, 1);
  assert.equal(findings[0].kind, "missing");
  assert.equal(findings[0].subject, "Кровля");
});

test("a level only the link has is named too", () => {
  const findings = compareLevels([level("1 этаж", 0)], [level("1 этаж", 0), level("Фундамент", -2700)]);

  assert.equal(findings.length, 1);
  assert.equal(findings[0].kind, "extra");
  assert.equal(findings[0].subject, "Фундамент");
});

test("the same height under two names is a finding, not a match", () => {
  // The live case: our «2 этаж» and their «Уровень 2» at the same elevation. Nobody
  // spots this by eye, and every later reference to a level by name goes wrong.
  const findings = compareLevels([level("2 этаж", 3900)], [level("Уровень 2", 3900)]);

  assert.equal(findings.length, 1);
  assert.equal(findings[0].kind, "mismatch");
  assert.match(findings[0].text, /2 этаж/);
  assert.match(findings[0].text, /Уровень 2/);
});

test("a name match wins over a height match", () => {
  // Otherwise «2 этаж» pairs with whatever happens to sit at the same height and the
  // real difference in its own elevation goes unreported.
  const findings = compareLevels(
    [level("2 этаж", 3900)],
    [level("Антресоль", 3900), level("2 этаж", 3850)]
  );

  const named = findings.find((f) => f.subject === "2 этаж");
  assert.ok(named);
  assert.equal(named?.differenceMm, 50);
});

test("names are matched regardless of case and padding", () => {
  assert.deepEqual(compareLevels([level("2 Этаж", 3900)], [level(" 2 этаж ", 3900)]), []);
});

test("a few unmatched levels are still listed one by one", () => {
  const findings = compareLevels([level("1 этаж", 0), level("2 этаж", 3900)], []);

  assert.equal(findings.length, 2);
  assert.ok(findings.every((f) => f.kind === "missing"));
});

test("a whole different storey scheme is one line, not twenty", () => {
  // The live case: a КР with template levels against twenty floors of АР gave 25
  // findings, 24 of which said the same thing, and buried the one that mattered.
  const ours = Array.from({ length: 20 }, (_, i) => level(`${i + 1} этаж`, i * 3000));
  const findings = compareLevels(ours, []);

  assert.equal(findings.length, 1);
  assert.equal(findings[0].kind, "missing");
  assert.match(findings[0].text, /20/);
  assert.match(findings[0].text, /1 этаж/);
  assert.match(findings[0].text, /и ещё 17/);
});

test("folding the noise does not hide a real difference", () => {
  // The name clash has to survive twenty unmatched levels around it.
  const ours = [level("1 этаж", 0), ...Array.from({ length: 20 }, (_, i) => level(`${i + 2} этаж`, (i + 1) * 3000))];
  const findings = compareLevels(ours, [level("Уровень 1", 0)]);

  const clash = findings.find((f) => f.text.includes("Уровень 1"));
  assert.ok(clash, "name clash must be reported");
  assert.ok(findings.length <= 3, `expected a short report, got ${findings.length}`);
});

// --- grids ------------------------------------------------------------------

test("a shifted grid is reported with its name and the offset", () => {
  const findings = compareGrids([grid("1", 0)], [grid("1", 300)]);

  assert.equal(findings.length, 1);
  assert.equal(findings[0].subject, "1");
  assert.equal(findings[0].differenceMm, 300);
});

test("grids that agree produce nothing", () => {
  assert.deepEqual(compareGrids([grid("1", 0), grid("2", 6000)], [grid("1", 0), grid("2", 6000)]), []);
});

test("a grid drawn from the other end is the same grid", () => {
  // Otherwise every second axis reads as twenty metres out.
  const ours = grid("А", 0);
  const theirs = { name: "А", startMm: ours.endMm, endMm: ours.startMm };

  assert.equal(gridOffsetMm(ours, theirs), 0);
  assert.deepEqual(compareGrids([ours], [theirs]), []);
});

test("a link with no grids says so once, not per axis", () => {
  // Forty findings that all say the same thing is how a check stops being read.
  const findings = compareGrids([grid("1", 0), grid("2", 6000), grid("3", 12000)], []);

  assert.equal(findings.length, 1);
  assert.match(findings[0].text, /ни одной оси/);
  assert.match(findings[0].text, /3/);
});

test("a curved grid is flagged for a human rather than measured", () => {
  const ours = { ...grid("1", 0), isCurved: true };
  const findings = compareGrids([ours], [grid("1", 0)]);

  assert.equal(findings.length, 1);
  assert.match(findings[0].text, /вручную/);
});

test("a small drafting difference is inside tolerance", () => {
  assert.deepEqual(compareGrids([grid("1", 0)], [grid("1", 2)]), []);
});

test("a shifted axis is not buried under the ones nobody drew", () => {
  // The live run: one axis genuinely out of place, and twelve rows saying the КР has
  // no grid «2», «3», «4»… The one that matters has to stay readable.
  const ours = ["1", "2", "3", "4", "5", "6", "7", "А", "Б", "В"].map((name, i) => grid(name, i * 6000));
  const findings = compareGrids(ours, [grid("1", 3000)]);

  const shifted = findings.find((f) => f.kind === "mismatch");
  assert.ok(shifted, "the shifted axis must be reported");
  assert.equal(shifted?.subject, "1");
  assert.equal(shifted?.differenceMm, 3000);

  assert.ok(findings.length <= 2, `expected a short report, got ${findings.length}`);
  const folded = findings.find((f) => f.kind === "missing");
  assert.match(folded!.text, /9/);
});

test("a few missing axes are still listed by name", () => {
  const findings = compareGrids([grid("1", 0), grid("2", 6000)], [grid("1", 0)]);

  assert.equal(findings.length, 1);
  assert.equal(findings[0].subject, "2");
});

test("an axis only the link has is named", () => {
  const findings = compareGrids([grid("1", 0)], [grid("1", 0), grid("2", 6000)]);

  assert.equal(findings.length, 1);
  assert.equal(findings[0].kind, "extra");
  assert.equal(findings[0].subject, "2");
});

// --- points and placement ---------------------------------------------------

test("a moved base point is reported with the distance", () => {
  const findings = comparePoints(
    { projectBasePointMm: { x: 0, y: 0, z: 0 } },
    { projectBasePointMm: { x: 300, y: 400, z: 0 } }
  );

  assert.equal(findings.length, 1);
  assert.equal(findings[0].differenceMm, 500);
});

test("a point only one side has is not a finding", () => {
  // Half a comparison is not a difference; saying so would be inventing one.
  assert.deepEqual(comparePoints({ projectBasePointMm: { x: 0, y: 0, z: 0 } }, {}), []);
});

test("a link nudged off the origin is caught", () => {
  const findings = comparePlacement({
    originShared: false,
    originMm: { x: 0, y: 0, z: 150 },
  });

  assert.equal(findings.length, 1);
  assert.equal(findings[0].differenceMm, 150);
  assert.match(findings[0].text, /внутренних координат/);
});

test("a mirrored or rotated link is caught", () => {
  assert.equal(comparePlacement({ mirrored: true, originShared: true }).length, 1);
  assert.equal(comparePlacement({ rotationDegrees: 90, originShared: true }).length, 1);
});

test("a link inserted origin-to-origin is silent", () => {
  assert.deepEqual(
    comparePlacement({ originShared: true, rotationDegrees: 0, mirrored: false }),
    []
  );
});

// --- the message ------------------------------------------------------------

test("a clean check is one short sentence", () => {
  // The requirement from the ticket: «всё совпало → короткое, а не портянка».
  const message = buildSiteMessage("кж тест.rvt", [], ["уровни", "оси"]);

  assert.match(message, /расхождений нет/);
  assert.ok(message.length < 120);
});

test("the summary counts by area rather than listing everything", () => {
  const findings = [
    ...compareLevels([level("2 этаж", 3900)], [level("2 этаж", 3850)]),
    ...compareGrids([grid("1", 0)], [grid("1", 300)]),
  ];

  const message = buildSiteMessage("кж тест.rvt", findings, ["уровни", "оси"]);
  assert.match(message, /расхождений 2/);
  assert.match(message, /уровни — 1/);
  assert.match(message, /оси — 1/);
});
