import assert from "node:assert/strict";
import { beforeEach, describe, it } from "node:test";
import Database from "better-sqlite3";
import type { NormativeRule } from "./types.js";
import {
  buildRuleKey,
  compactRulesForMcp,
  getNormLibraryStats,
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

  it("ranks numeric corridor width above «световой карман» definition", () => {
    const width = makeCorridorRule({
      source: {
        document: "СП РК 3.02-101",
        clause: "4.2.21",
        quote:
          "Ширина эвакуационных коридоров должна быть не менее 1,2 м.",
      },
    });
    const pocket = makeCorridorRule({
      id: "rule-pocket",
      type: "note",
      object: "световой карман",
      value: "определение",
      unit: "none",
      normalized: undefined,
      source: {
        document: "СП РК 3.02-101",
        clause: "п. 3.13",
        quote:
          "Световой карман: помещение, примыкающее к коридору и служащее для его освещения. Роль светового кармана может выполнять лестничная клетка, отделённая от коридора или с дверью шириной не менее 1,2 м.",
      },
    });
    saveNormRules(db, [pocket, width]);

    const ranked = queryNormRules(db, { topic: "ширина эвакуационного коридора" });
    assert.ok(ranked.length >= 1);
    assert.equal(ranked[0].object, "коридор");
    assert.equal(ranked[0].type, "min_value");
    assert.equal(ranked[0].source.clause, "4.2.21");
    assert.match(ranked[0].source.quote, /эвакуационн/i);
  });

  it("ranks balcony min size ahead of unrelated note mentioning балкон", () => {
    const balcony = makeCorridorRule({
      id: "rule-balcony",
      object: "лоджия",
      value: 1.2,
      source: {
        document: "СП РК 3.02-101",
        clause: "5.4.1",
        quote: "Глубина лоджии должна быть не менее 1,2 м.",
      },
      normalized: { min: 1200 },
    });
    const note = makeCorridorRule({
      id: "rule-note",
      type: "note",
      object: "фасад",
      value: "примечание",
      unit: "none",
      normalized: undefined,
      source: {
        document: "СП РК 3.02-101",
        clause: "1.1",
        quote: "На фасаде допускаются балконы и лоджии по проекту.",
      },
    });
    saveNormRules(db, [note, balcony]);
    const ranked = queryNormRules(db, { topic: "минимальная глубина лоджии" });
    assert.equal(ranked[0].object, "лоджия");
    assert.equal(ranked[0].type, "min_value");
  });

  it("exposes library document stats", () => {
    saveNormRules(db, [
      makeCorridorRule(),
      makeCorridorRule({
        object: "дверь",
        source: {
          document: "ГОСТ 21.101-97",
          clause: "5.1",
          quote: "Высота строки основной надписи не менее 5 мм.",
        },
      }),
    ]);
    const stats = getNormLibraryStats(db);
    assert.equal(stats.ruleCount, 2);
    assert.equal(stats.documentCount, 2);
    assert.ok(stats.documents.some((d) => d.document === "СП РК 3.02-101"));
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

  it("compacts MCP payload: truncates long quotes and drops bulky fields", () => {
    const longQuote = "Ширина коридора. ".repeat(80);
    saveNormRules(db, [
      makeCorridorRule({
        source: {
          document: "СП РК",
          clause: "п. 1",
          quote: longQuote,
        },
      }),
    ]);
    const rules = queryNormRules(db, { topic: "коридор" });
    const compact = compactRulesForMcp(rules);
    assert.equal(compact.length, 1);
    assert.ok(compact[0].source.quote.length < longQuote.length);
    assert.equal(compact[0].source.quoteTruncated, true);
    assert.ok(!("tags" in compact[0]));
    assert.ok(!("createdAt" in compact[0]));
  });
});
