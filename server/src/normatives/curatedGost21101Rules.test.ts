import assert from "node:assert/strict";
import { describe, it } from "node:test";
import Database from "better-sqlite3";
import {
  CURATED_TITLE_BLOCK_LINE_HEIGHT_RULE_ID,
  curatedGost21101Rules,
  ensureCuratedGost21101Rules,
} from "./curatedGost21101Rules.js";
import { queryNormRules, saveNormRules } from "./rulesStore.js";
import type { SaveableNormRule } from "./rulesStore.js";

describe("curatedGost21101Rules", () => {
  it("defines stamp line-height and sheet format numeric rules", () => {
    const rules = curatedGost21101Rules();
    assert.equal(rules.length, 2);

    const stamp = rules.find(
      (r) => r.id === CURATED_TITLE_BLOCK_LINE_HEIGHT_RULE_ID
    );
    assert.ok(stamp);
    assert.equal(stamp?.object, "основная надпись");
    assert.equal(stamp?.type, "min_value");
    assert.equal(stamp?.unit, "mm");
    assert.equal(stamp?.normalized?.exact, 5);
    assert.match(stamp?.source.clause ?? "", /5\.1\.4/);
  });

  it("persists and ranks above noise for query «основная надпись»", () => {
    const db = new Database(":memory:");
    ensureCuratedGost21101Rules(db);

    const noise: SaveableNormRule = {
      id: "noise-atm-nadpis",
      type: "requirement",
      object: "объект",
      value:
        "На дисплее банкомата все надписи должны отображаться крупным шрифтом.",
      unit: "none",
      applicability: null,
      source: {
        document: "99_СН РК 3.06-01-2011",
        clause: "п. 5.3.7.6",
        quote:
          "На дисплее банкомата все надписи должны отображаться крупным шрифтом.",
      },
      tags: ["надписи"],
    };
    saveNormRules(db, [noise]);

    const hits = queryNormRules(db, { topic: "основная надпись", limit: 3 });
    assert.ok(hits.length >= 1);
    assert.equal(hits[0]?.object, "основная надпись");
    assert.equal(hits[0]?.source.document, "ГОСТ 21.101-97");
    assert.equal(hits[0]?.normalized?.exact, 5);
  });
});
