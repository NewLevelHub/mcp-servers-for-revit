import assert from "node:assert/strict";
import { beforeEach, describe, it } from "node:test";
import Database from "better-sqlite3";
import type { BriefRequirement } from "./types.js";
import {
  buildRequirementKey,
  getBriefLibraryStats,
  listBriefRequirementsByType,
  queryBriefRequirements,
  saveBriefRequirements,
} from "./briefStore.js";

function makeStudioRequirement(overrides: Partial<BriefRequirement> = {}): BriefRequirement {
  return {
    id: "req-1",
    type: "room_count",
    object: "студия",
    value: 25,
    unit: "pcs",
    source: {
      document: "ТЗ ЖК «Сарыарка»",
      clause: "",
      quote: "Студия — 25 шт.",
    },
    ...overrides,
  };
}

describe("briefStore", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = new Database(":memory:");
  });

  it("inserts a new requirement and finds it by topic", () => {
    const result = saveBriefRequirements(db, [makeStudioRequirement()], { documentVersion: "12.03.2026" });
    assert.equal(result.inserted, 1);
    assert.equal(result.updated, 0);

    const found = queryBriefRequirements(db, { topic: "студии" });
    assert.equal(found.length, 1);
    assert.equal(found[0].object, "студия");
    assert.equal(found[0].documentVersion, "12.03.2026");
  });

  it("re-saving the same document+clause+object+type updates instead of duplicating", () => {
    saveBriefRequirements(db, [makeStudioRequirement()]);
    const second = saveBriefRequirements(db, [makeStudioRequirement({ value: 30 })]);
    assert.equal(second.inserted, 0);
    assert.equal(second.updated, 1);

    const found = queryBriefRequirements(db, { topic: "студия" });
    assert.equal(found.length, 1);
    assert.equal(found[0].value, 30);
  });

  it("a topic that matches nothing returns an empty list, not a guess", () => {
    saveBriefRequirements(db, [makeStudioRequirement()]);
    const found = queryBriefRequirements(db, { topic: "паркинг" });
    assert.equal(found.length, 0);
  });

  it("document filter narrows results", () => {
    saveBriefRequirements(db, [
      makeStudioRequirement(),
      makeStudioRequirement({ id: "req-2", source: { document: "Другое ТЗ", clause: "", quote: "Студия — 5 шт." } }),
    ]);
    const found = queryBriefRequirements(db, { topic: "студия", document: "Сарыарка" });
    assert.equal(found.length, 1);
    assert.equal(found[0].source.document, "ТЗ ЖК «Сарыарка»");
  });

  it("listBriefRequirementsByType is the input check_against_brief reads", () => {
    saveBriefRequirements(db, [
      makeStudioRequirement(),
      makeStudioRequirement({
        id: "req-3",
        type: "room_area_min",
        object: "кладовая",
        value: 3,
        unit: "m2",
        source: { document: "ТЗ ЖК «Сарыарка»", clause: "", quote: "Кладовая не менее 3 м²." },
      }),
    ]);

    assert.equal(listBriefRequirementsByType(db, "room_count").length, 1);
    assert.equal(listBriefRequirementsByType(db, "room_area_min").length, 1);
    assert.equal(listBriefRequirementsByType(db, "note").length, 0);
  });

  it("buildRequirementKey is form-insensitive (case/ё)", () => {
    const a = buildRequirementKey(makeStudioRequirement());
    const b = buildRequirementKey(makeStudioRequirement({ object: "СТУДИЯ" }));
    assert.equal(a, b);
  });

  it("getBriefLibraryStats counts requirements and distinct documents", () => {
    saveBriefRequirements(db, [
      makeStudioRequirement(),
      makeStudioRequirement({ id: "req-2", source: { document: "Другое ТЗ", clause: "", quote: "x" } }),
    ]);
    const stats = getBriefLibraryStats(db);
    assert.equal(stats.requirementCount, 2);
    assert.equal(stats.documentCount, 2);
  });
});
