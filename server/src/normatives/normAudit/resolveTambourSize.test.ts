import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { StoredNormRule } from "../rulesStore.js";
import {
  pickBestTambourSizeRule,
  scoreTambourSizeRule,
} from "./resolveTambourSize.js";

function rule(
  partial: Partial<StoredNormRule> & Pick<StoredNormRule, "id">
): StoredNormRule {
  return {
    ruleKey: partial.id,
    tags: [],
    documentVersion: null,
    createdAt: 0,
    updatedAt: 0,
    type: partial.type ?? "min_value",
    object: partial.object ?? "тамбур",
    value: partial.value ?? 1.65,
    unit: partial.unit ?? "m",
    source: partial.source ?? {
      document: "СП РК 3.02-101-2012",
      clause: "п. 4.4.10.6",
      quote:
        "Негізгі кіреберіс тамбурының көлемі кемінде 1,65 м × 1,65 м болуы тиіс.",
    },
    applicability: partial.applicability ?? null,
    normalized: partial.normalized,
    ...partial,
  };
}

describe("pickBestTambourSizeRule", () => {
  it("accepts entrance tambour 1.65 m minimum", () => {
    const tambourRule = rule({
      id: "tambour-entrance",
      object: "площадь",
      normalized: { min: 1650 },
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4.10.6",
        quote:
          "Негізгі кіреберіс тамбурының көлемі кемінде 1,65 м × 1,65 м болуы тиіс.",
      },
    });

    const picked = pickBestTambourSizeRule([tambourRule]);
    assert.ok(picked);
    assert.equal(picked!.id, "tambour-entrance");
    assert.ok(scoreTambourSizeRule(tambourRule) > 0);
  });

  it("rejects garbage-container tambour rule", () => {
    const garbage = rule({
      id: "garbage",
      object: "тамбур",
      normalized: { min: 1650 },
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4.10.7",
        quote: "Мусорный контейнер в тамбуре не менее 1,65 м.",
      },
    });

    assert.equal(scoreTambourSizeRule(garbage), -1000);
    assert.equal(pickBestTambourSizeRule([garbage]), null);
  });

  it("rejects corridor width without tambour context", () => {
    const corridor = rule({
      id: "corridor",
      object: "коридор",
      normalized: { min: 1650 },
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 6.3.1",
        quote: "Ширина коридора не менее 1,65 м.",
      },
    });

    assert.equal(scoreTambourSizeRule(corridor), -1000);
  });
});
