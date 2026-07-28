import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  selectPhase1Checkers,
  selectSkippedRules,
  topicMatchesHints,
} from "./checklist.js";

describe("normAudit checklist", () => {
  it("runs all default Phase-1 checkers when topics omitted (МГН opt-in excluded)", () => {
    const selected = selectPhase1Checkers();
    assert.equal(selected.length, 15);
    assert.deepEqual(
      selected.map((c) => c.checkType),
      [
        "evacuation_width",
        "room_depth",
        "min_dimensions",
        "fire_doors",
        "door_clear_width",
        "tambour_size_min",
        "room_area_min",
        "room_height_min",
        "storey_height",
        "window_sill_height",
        "opening_height",
        "stair_width",
        "stair_riser_tread",
        "ramp_slope_width",
        "railing_height",
      ]
    );
  });

  it("filters checkers by topic", () => {
    const corridors = selectPhase1Checkers(["эвак. коридор"]);
    assert.equal(corridors.length, 1);
    assert.equal(corridors[0].checkType, "evacuation_width");

    const loggia = selectPhase1Checkers(["лоджия"]);
    assert.equal(loggia.length, 1);
    assert.equal(loggia[0].checkType, "min_dimensions");
  });

  it("surfaces Phase-2 skipped on full audit", () => {
    const skipped = selectSkippedRules();
    assert.ok(skipped.some((s) => s.checkType === "egress_opening_width"));
  });

  it("runs the door-width checker when user asks about doors", () => {
    const selected = selectPhase1Checkers(["ширина двери"]);
    assert.ok(selected.some((c) => c.checkType === "door_clear_width"));
    // «ширина двери» is now a real checker, not a Phase-2 skip.
    const skipped = selectSkippedRules(["ширина двери"]);
    assert.ok(!skipped.some((s) => s.checkType === "door_clear_width"));
  });

  it("does not mark clear-opening «в свету» as skipped", () => {
    const skipped = selectSkippedRules(["дверь в свету"]);
    assert.ok(!skipped.some((s) => s.checkType === "door_clear_width"));
  });

  it("runs the tambour checker when user asks about тамбур size", () => {
    const selected = selectPhase1Checkers(["тамбур 1.65"]);
    assert.ok(selected.some((c) => c.checkType === "tambour_size_min"));
  });

  it("runs sill / opening checkers when user asks about windows / exits", () => {
    const sill = selectPhase1Checkers(["подоконник"]);
    assert.ok(sill.some((c) => c.checkType === "window_sill_height"));

    const opening = selectPhase1Checkers(["высота проёма"]);
    assert.ok(opening.some((c) => c.checkType === "opening_height"));
  });

  it("runs stair / ramp / railing checkers by topic", () => {
    const stair = selectPhase1Checkers(["ширина марша"]);
    assert.ok(stair.some((c) => c.checkType === "stair_width"));

    const ramp = selectPhase1Checkers(["пандус"]);
    assert.ok(ramp.some((c) => c.checkType === "ramp_slope_width"));

    const rail = selectPhase1Checkers(["ограждение"]);
    assert.ok(rail.some((c) => c.checkType === "railing_height"));
  });

  it("topicMatchesHints is bidirectional substring", () => {
    assert.equal(topicMatchesHints(["коридор"], ["эвак", "коридор"]), true);
    assert.equal(topicMatchesHints(["foobar"], ["коридор"]), false);
  });
});
