import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  selectPhase1Checkers,
  selectSkippedRules,
  topicMatchesHints,
} from "./checklist.js";

describe("normAudit checklist", () => {
  it("runs all Phase-1 checkers when topics omitted", () => {
    const selected = selectPhase1Checkers();
    assert.equal(selected.length, 9);
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
    assert.ok(skipped.some((s) => s.checkType === "door_clear_width"));
  });

  it("runs the door-width checker when user asks about doors", () => {
    const selected = selectPhase1Checkers(["ширина двери"]);
    assert.ok(selected.some((c) => c.checkType === "door_clear_width"));
    // «ширина двери» is now a real checker, not a Phase-2 skip.
    const skipped = selectSkippedRules(["ширина двери"]);
    assert.ok(!skipped.some((s) => s.checkType === "door_clear_width"));
  });

  it("still surfaces the clear-opening «в свету» follow-up as skipped", () => {
    const skipped = selectSkippedRules(["дверь в свету"]);
    assert.ok(skipped.some((s) => s.checkType === "door_clear_width"));
  });

  it("runs the tambour checker when user asks about тамбур size", () => {
    const selected = selectPhase1Checkers(["тамбур 1.65"]);
    assert.ok(selected.some((c) => c.checkType === "tambour_size_min"));
  });

  it("topicMatchesHints is bidirectional substring", () => {
    assert.equal(topicMatchesHints(["коридор"], ["эвак", "коридор"]), true);
    assert.equal(topicMatchesHints(["foobar"], ["коридор"]), false);
  });
});
