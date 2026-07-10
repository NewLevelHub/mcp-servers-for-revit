import assert from "node:assert/strict";
import { beforeEach, describe, it } from "node:test";
import Database from "better-sqlite3";
import type { NormativeRule } from "./types.js";
import {
  buildRuleKey,
  queryNormRules,
  saveNormRules,
  withSuggestedTags,
} from "./rulesStore.js";

function makeCorridorRule(overrides: Partial<NormativeRule> = {}): NormativeRule {
  return {
    id: "rule-1",
    type: "min_value",
    object: "коридор",
    value: 1.2,
    unit: "m",
    applicability: {
      raw: "в жилых зданиях",
      buildingType: "жилое здание",
    },
    source: {
      document: "СП РК 3.02-101",
      clause: "5.2.4",
      quote: "Ширина коридора должна быть не менее 1,2 м.",
      page: 12,
    },
    normalized: { min: 1200 },
    ...overrides,
  };
}

describe("rulesStore", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = new Database(":memory:");
  });

  it("inserts new rules and finds them by a different word form", () => {
    const result = saveNormRules(db, [makeCorridorRule()], {
      documentVersion: "27.04.2021",
    });
    assert.equal(result.inserted, 1);
    assert.equal(result.updated, 0);

    const byTopic = queryNormRules(db, { topic: "ширина коридоров" });
    assert.equal(byTopic.length, 1);
    assert.equal(byTopic[0].object, "коридор");
    assert.equal(byTopic[0].documentVersion, "27.04.2021");
    assert.equal(byTopic[0].source.quote, "Ширина коридора должна быть не менее 1,2 м.");
    assert.deepEqual(byTopic[0].normalized, { min: 1200 });

    const byUpperCase = queryNormRules(db, { topic: "КОРИДОР" });
    assert.equal(byUpperCase.length, 1);
  });

  it("deduplicates by document+clause+object+type and updates the value", () => {
    saveNormRules(db, [makeCorridorRule()]);
    const second = saveNormRules(db, [
      makeCorridorRule({ value: 1.5, normalized: { min: 1500 } }),
    ]);

    assert.equal(second.inserted, 0);
    assert.equal(second.updated, 1);

    const rules = queryNormRules(db, { topic: "коридор" });
    assert.equal(rules.length, 1);
    assert.equal(rules[0].value, 1.5);
    assert.deepEqual(rules[0].normalized, { min: 1500 });
  });

  it("keeps rules from the same clause but different object as separate rows", () => {
    const corridor = makeCorridorRule();
    const ceiling = makeCorridorRule({
      object: "высота потолка",
      value: 2.5,
      source: {
        document: "СП РК 3.02-101",
        clause: "5.2.4",
        quote: "Высота потолка должна быть не менее 2,5 м.",
      },
      normalized: { min: 2500 },
    });
    assert.notEqual(buildRuleKey(corridor), buildRuleKey(ceiling));

    const result = saveNormRules(db, [corridor, ceiling]);
    assert.equal(result.inserted, 2);

    const rules = queryNormRules(db, { topic: "5.2.4" });
    assert.equal(rules.length, 2);
  });

  it("updates document version on re-save and keeps it when omitted", () => {
    saveNormRules(db, [makeCorridorRule()], { documentVersion: "2012" });
    saveNormRules(db, [makeCorridorRule()], { documentVersion: "27.04.2021" });

    let rules = queryNormRules(db, { topic: "коридор" });
    assert.equal(rules[0].documentVersion, "27.04.2021");

    saveNormRules(db, [makeCorridorRule({ value: 1.4 })]);
    rules = queryNormRules(db, { topic: "коридор" });
    assert.equal(rules[0].documentVersion, "27.04.2021");
    assert.equal(rules[0].value, 1.4);
  });

  it("filters by document and rule type", () => {
    saveNormRules(db, [
      makeCorridorRule(),
      makeCorridorRule({
        type: "max_value",
        object: "уклон пандуса",
        value: 8,
        unit: "percent",
        source: {
          document: "ГОСТ 21.101-97",
          clause: "4.1.2",
          quote: "Уклон пандуса не должен превышать 8 %.",
        },
      }),
    ]);

    const byDocument = queryNormRules(db, {
      topic: "уклон",
      document: "гост 21.101",
    });
    assert.equal(byDocument.length, 1);
    assert.equal(byDocument[0].source.document, "ГОСТ 21.101-97");

    const wrongDocument = queryNormRules(db, {
      topic: "уклон",
      document: "СП РК 3.02-101",
    });
    assert.equal(wrongDocument.length, 0);

    const byType = queryNormRules(db, { topic: "коридор", ruleType: "min_value" });
    assert.equal(byType.length, 1);
    const byWrongType = queryNormRules(db, {
      topic: "коридор",
      ruleType: "prohibition",
    });
    assert.equal(byWrongType.length, 0);
  });

  it("matches when only part of the topic terms are present, ranking fuller matches first", () => {
    const corridor = makeCorridorRule();
    const door = makeCorridorRule({
      object: "дверь",
      value: 0.9,
      source: {
        document: "СП РК 3.02-101",
        clause: "5.3.1",
        quote: "Ширина дверного проема должна быть не менее 0,9 м.",
      },
      normalized: { min: 900 },
    });
    saveNormRules(db, [corridor, door]);

    // "эвакуационного" is absent from both rules, but "коридора" matches one.
    const partial = queryNormRules(db, { topic: "ширина эвакуационного коридора" });
    assert.equal(partial.length, 2);
    assert.equal(partial[0].object, "коридор");
  });

  it("returns empty list when nothing matches", () => {
    saveNormRules(db, [makeCorridorRule()]);
    const rules = queryNormRules(db, { topic: "вентиляция машинного зала" });
    assert.equal(rules.length, 0);
  });

  it("finds rules by semantic tags in both languages", () => {
    saveNormRules(db, [
      {
        ...makeCorridorRule(),
        tags: ["проход", "путь эвакуации", "дәліз", "дәліз ені"],
      },
    ]);

    const bySynonym = queryNormRules(db, { topic: "ширина прохода" });
    assert.equal(bySynonym.length, 1);
    assert.deepEqual(bySynonym[0].tags, [
      "проход",
      "путь эвакуации",
      "дәліз",
      "дәліз ені",
    ]);

    const byKazakh = queryNormRules(db, { topic: "дәліз" });
    assert.equal(byKazakh.length, 1);
  });

  it("keeps tags searchable when a rule is re-saved without tags", () => {
    saveNormRules(db, [{ ...makeCorridorRule(), tags: ["проход"] }]);
    saveNormRules(db, [makeCorridorRule({ value: 1.5 })]);

    const rules = queryNormRules(db, { topic: "проход" });
    assert.equal(rules.length, 1);
    assert.equal(rules[0].value, 1.5);
    assert.deepEqual(rules[0].tags, ["проход"]);

    saveNormRules(db, [{ ...makeCorridorRule(), tags: ["галерея"] }]);
    const replaced = queryNormRules(db, { topic: "галерея" });
    assert.equal(replaced.length, 1);
    assert.deepEqual(replaced[0].tags, ["галерея"]);
  });

  it("suggests default tags when none are provided on save", () => {
    const tagged = withSuggestedTags([
      {
        ...makeCorridorRule(),
        object: "основная надпись",
        source: {
          document: "ГОСТ 21.101-97",
          clause: "п. 5.1.4",
          quote: "Основная надпись выполняется с высотой строки не менее 5 мм.",
        },
      },
    ]);
    assert.ok(tagged[0].tags?.some((tag) => /штамп/i.test(tag)));
    saveNormRules(db, tagged, { documentVersion: "97" });
    const found = queryNormRules(db, { topic: "штамп чертежа" });
    assert.equal(found.length, 1);
    assert.equal(found[0].documentVersion, "97");
  });

  it("migrates a pre-tags norm_rules table", () => {
    db.exec(`
      CREATE TABLE norm_rules (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        rule_key TEXT NOT NULL UNIQUE,
        document TEXT NOT NULL,
        document_norm TEXT NOT NULL,
        document_version TEXT,
        clause TEXT NOT NULL,
        rule_type TEXT NOT NULL,
        object TEXT NOT NULL,
        value_json TEXT NOT NULL,
        unit TEXT NOT NULL,
        applicability_json TEXT,
        normalized_json TEXT,
        quote TEXT NOT NULL,
        page INTEGER,
        search_text TEXT NOT NULL,
        created_at INTEGER NOT NULL,
        updated_at INTEGER NOT NULL
      )
    `);

    const result = saveNormRules(db, [
      { ...makeCorridorRule(), tags: ["проход"] },
    ]);
    assert.equal(result.inserted, 1);

    const rules = queryNormRules(db, { topic: "проход" });
    assert.equal(rules.length, 1);
    assert.deepEqual(rules[0].tags, ["проход"]);
  });
});
