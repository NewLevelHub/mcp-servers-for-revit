import test from "node:test";
import assert from "node:assert/strict";
import {
  MANEUVERING_CLAUSE,
  annotateDoorEgressResponse,
  annotateDoorManeuvering,
} from "./annotateDoorManeuvering.js";

/** Real rows from «Короткий блок», 2 этаж, 18.08.2026. */
const REAL_DOORS = {
  /** Воздушная зона 164 — the one the model called out, correctly. */
  airZone: {
    id: 1786174,
    type: "(дверь)ДОмп_П_2100-1200",
    maneuveringDepthMm: 4574.999999999981,
    maneuveringWidthMm: 1375,
    maneuveringRequiredDepthMm: 1500,
    maneuveringApproach: "pull/family-facing",
  },
  /** Лифтовый холл 165 — the identical failure the model called "всё в порядке". */
  liftHall: {
    id: 672691,
    type: "(дверь)ДОмп_Л_2100-1200",
    maneuveringDepthMm: 6174.999999999961,
    maneuveringWidthMm: 1450,
    maneuveringRequiredDepthMm: 1500,
    maneuveringApproach: "pull/family-facing",
  },
  /** Тамбур 2403 — depth exactly on the limit. */
  vestibule: {
    id: 672770,
    type: "(дверь)ДОмп_Л_2100-1200",
    maneuveringDepthMm: 1500,
    maneuveringWidthMm: 1675,
    maneuveringRequiredDepthMm: 1500,
    maneuveringApproach: "pull/family-facing",
  },
  /** Прихожая 183 — comfortable on both. */
  hallway: {
    id: 667528,
    type: "(дверь)ДСВ_П_М2_2100-1050",
    maneuveringDepthMm: 3174.999999999994,
    maneuveringWidthMm: 2300,
    maneuveringRequiredDepthMm: 1200,
    maneuveringApproach: "push/opposite-facing",
  },
};

test("a narrow approach fails even when depth is ample", () => {
  // 4575 mm of depth against 1500 required, but 1375 of width against 1500.
  const v = annotateDoorManeuvering(REAL_DOORS.airZone);
  assert.equal(v.maneuveringVerdict, "violation");
  assert.equal(v.maneuveringDeviationMm, 125);
  assert.match(v.maneuveringNote, /ширина 1375 < 1500/);
  // Only the failing limit is listed. Checked against the reasons, not the whole
  // note: the quoted clause itself talks about глубина.
  const reasons = v.maneuveringNote.split(":")[1] ?? "";
  assert.doesNotMatch(reasons, /глубина/, "глубина проходит — её не перечислять");
});

test("the door the model waved through fails the same way", () => {
  // This is the regression: same rule, same side, 1450 < 1500 — reported as fine.
  const v = annotateDoorManeuvering(REAL_DOORS.liftHall);
  assert.equal(v.maneuveringVerdict, "violation");
  assert.equal(v.maneuveringDeviationMm, 50);
});

test("both real violations are found by the same rule", () => {
  for (const door of [REAL_DOORS.airZone, REAL_DOORS.liftHall]) {
    assert.equal(annotateDoorManeuvering(door).maneuveringVerdict, "violation", `дверь ${door.id}`);
  }
});

test("exactly on the limit is borderline, not a pass", () => {
  const v = annotateDoorManeuvering(REAL_DOORS.vestibule);
  assert.equal(v.maneuveringVerdict, "near_limit");
  assert.equal(v.maneuveringDeviationMm, 0);
  assert.match(v.maneuveringNote, /впритык/i);
});

test("comfortable on both limits passes with no note", () => {
  const v = annotateDoorManeuvering(REAL_DOORS.hallway);
  assert.equal(v.maneuveringVerdict, "ok");
  assert.equal(v.maneuveringNote, "");
});

test("the push side keeps its own, smaller depth requirement", () => {
  // 1400 mm clears the 1200 push limit with room to spare; the same door judged
  // against the 1500 pull limit would fail.
  const v = annotateDoorManeuvering({
    maneuveringDepthMm: 1400,
    maneuveringWidthMm: 2000,
    maneuveringRequiredDepthMm: 1200,
  });
  assert.equal(v.maneuveringVerdict, "ok");

  const asPull = annotateDoorManeuvering({
    maneuveringDepthMm: 1400,
    maneuveringWidthMm: 2000,
    maneuveringRequiredDepthMm: 1500,
  });
  assert.equal(asPull.maneuveringVerdict, "violation");
});

test("a margin inside the tolerance reads as borderline, not as passing", () => {
  // 50 mm of headroom is exactly NEAR_LIMIT_TOLERANCE_MM — a door this tight is
  // one planning tweak away from failing, and saying "ok" would hide that.
  const v = annotateDoorManeuvering({
    maneuveringDepthMm: 1250,
    maneuveringWidthMm: 2000,
    maneuveringRequiredDepthMm: 1200,
  });
  assert.equal(v.maneuveringVerdict, "near_limit");
});

test("a missing required depth falls back to the stricter pull limit", () => {
  const v = annotateDoorManeuvering({
    maneuveringDepthMm: 1300,
    maneuveringWidthMm: 2000,
  });
  assert.equal(v.maneuveringVerdict, "violation");
  assert.match(v.maneuveringNote, /глубина 1300 < 1500/);
});

test("an unmeasured door says so instead of passing silently", () => {
  for (const door of [
    { maneuveringDepthMm: null, maneuveringWidthMm: 1600 },
    { maneuveringDepthMm: 1600, maneuveringWidthMm: 0 },
    {},
  ]) {
    const v = annotateDoorManeuvering(door);
    assert.equal(v.maneuveringVerdict, "not_measured");
    assert.match(v.maneuveringNote, /вручную/);
  }
});

test("both limits failing are both named", () => {
  const v = annotateDoorManeuvering({
    maneuveringDepthMm: 1000,
    maneuveringWidthMm: 1000,
    maneuveringRequiredDepthMm: 1500,
  });
  assert.match(v.maneuveringNote, /глубина 1000 < 1500/);
  assert.match(v.maneuveringNote, /ширина 1000 < 1500/);
  assert.equal(v.maneuveringDeviationMm, 500);
});

test("every verdict cites the clause it applied", () => {
  assert.match(
    annotateDoorManeuvering(REAL_DOORS.airZone).maneuveringNote,
    /СП РК 3\.06-101 п\. 4\.3\.2\.12/
  );
  assert.match(MANEUVERING_CLAUSE, /1,5 м при ширине не менее 1,5 м/);
});

// --- whole-response shape ---------------------------------------------------

test("the response summary counts what the rows say", () => {
  const out = annotateDoorEgressResponse({
    success: true,
    doors: [REAL_DOORS.airZone, REAL_DOORS.liftHall, REAL_DOORS.vestibule, REAL_DOORS.hallway],
  }) as Record<string, any>;

  assert.equal(out.maneuveringSummary.checked, 4);
  assert.equal(out.maneuveringSummary.violations, 2);
  assert.equal(out.maneuveringSummary.nearLimit, 1);
  assert.match(out.maneuveringSummary.note, /Не проходят по зоне манёвра МГН: 2 из 4/);
  assert.match(out.maneuveringSummary.note, /не сравнивай размеры сам/);
});

test("a clean set says so rather than staying silent", () => {
  const out = annotateDoorEgressResponse({ doors: [REAL_DOORS.hallway] }) as Record<string, any>;
  assert.equal(out.maneuveringSummary.violations, 0);
  assert.match(out.maneuveringSummary.note, /проходят/);
});

test("annotation keeps every original field", () => {
  const out = annotateDoorEgressResponse({
    success: true,
    doors: [REAL_DOORS.airZone],
  }) as Record<string, any>;

  assert.equal(out.success, true);
  assert.equal(out.doors[0].id, 1786174);
  assert.equal(out.doors[0].type, "(дверь)ДОмп_П_2100-1200");
  assert.equal(out.doors[0].maneuveringWidthMm, 1375);
  assert.equal(out.doors[0].maneuveringRequiredWidthMm, 1500);
});

test("a payload with no door list is returned untouched", () => {
  const empty = { success: false, message: "нет уровня" };
  assert.equal(annotateDoorEgressResponse(empty), empty);
  assert.equal(annotateDoorEgressResponse("text"), "text");
  assert.equal(annotateDoorEgressResponse(null), null);
});

test("the capitalised Doors key is handled too", () => {
  const out = annotateDoorEgressResponse({ Doors: [REAL_DOORS.airZone] }) as Record<string, any>;
  assert.equal(out.maneuveringSummary.checked, 1);
  assert.equal(out.Doors[0].maneuveringVerdict, "violation");
});
