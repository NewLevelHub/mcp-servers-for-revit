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
    assert.equal(selected.length, 4);
    assert.deepEqual(
      selected.map((c) => c.checkType),
      ["evacuation_width", "room_depth", "min_dimensions", "fire_doors"]
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

  it("surfaces door clear-width skip when user asks about doors", () => {
    const skipped = selectSkippedRules(["ширина двери"]);
    assert.ok(skipped.some((s) => s.checkType === "door_clear_width"));
  });

  it("topicMatchesHints is bidirectional substring", () => {
    assert.equal(topicMatchesHints(["коридор"], ["эвак", "коридор"]), true);
    assert.equal(topicMatchesHints(["foobar"], ["коридор"]), false);
  });
});
