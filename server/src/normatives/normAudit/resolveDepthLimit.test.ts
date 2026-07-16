import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { StoredNormRule } from "../rulesStore.js";
import { pickBestDepthRule } from "./resolveDepthLimit.js";

function rule(partial: Partial<StoredNormRule> & Pick<StoredNormRule, "id">): StoredNormRule {
  return {
    ruleKey: partial.id,
    tags: [],
    documentVersion: null,
    createdAt: 0,
    updatedAt: 0,
    type: partial.type ?? "max_value",
    object: partial.object ?? "помещение",
    value: partial.value ?? 6000,
    unit: partial.unit ?? "mm",
    source: partial.source ?? {
      document: "СП РК 3.02-101",
      clause: "п. 5.2",
      quote: "Глубина жилой комнаты не более 6 м.",
    },
    applicability: partial.applicability ?? null,
    normalized: partial.normalized,
    ...partial,
  };
}

describe("pickBestDepthRule", () => {
  it("prefers depth rules over kitchen area rules", () => {
    const kitchen = rule({
      id: "kitchen-area",
      object: "кухня",
      type: "min_value",
      value: 8,
      unit: "m2",
      normalized: { min: 8 },
      source: {
        document: "SP RK 3.06-31-2005",
        clause: "п. 5.10",
        quote:
          "Площадь кухни рекомендуется принимать в однокомнатных квартирах не менее 8 м2.",
      },
    });
    const depth = rule({
      id: "room-depth",
      object: "глубина жилой комнаты",
      type: "max_value",
      value: 6000,
      normalized: { max: 6000 },
      source: {
        document: "СП РК 3.02-101",
        clause: "п. 5.2.4",
        quote: "Максимальная глубина жилой комнаты не должна превышать 6 м.",
      },
    });

    const picked = pickBestDepthRule([kitchen, depth]);
    assert.ok(picked);
    assert.equal(picked!.id, "room-depth");
    assert.equal(picked!.normalized?.max, 6000);
  });

  it("rejects loggia width rules", () => {
    const loggia = rule({
      id: "loggia",
      object: "лоджия",
      normalized: { min: 1400 },
      source: {
        document: "СП РК 3.02-101",
        clause: "4.6.5",
        quote: "Ширина лоджии не менее 1,4 м.",
      },
    });
    const depth = rule({
      id: "depth",
      object: "глубина комнаты",
      normalized: { max: 6000 },
      source: {
        document: "СП РК 3.02-101",
        clause: "п. 5.2",
        quote: "Глубина жилой комнаты не более 6 м.",
      },
    });

    const picked = pickBestDepthRule([loggia, depth]);
    assert.equal(picked?.id, "depth");
  });

  it("rejects entryway width rule whose quote merely mentions «глубиной»", () => {
    // Real REV-54 false positive: «ширина передней ≥ 1,6 м» min rule whose
    // quote contains «шкафами глубиной 60 см» must NOT become a depth max.
    const entryway = rule({
      id: "entryway-width",
      object: "ширина передней",
      type: "min_value",
      value: 1600,
      normalized: { min: 1600 },
      source: {
        document: "SP RK 3.06-31-2005",
        clause: "п. 5.8",
        quote:
          "Ширина передней должна быть не менее 1,6 м; переднюю рекомендуется оборудовать встроенными шкафами глубиной 60 см.",
      },
    });

    assert.equal(pickBestDepthRule([entryway]), null);
  });

  it("rejects implausibly small depth max (1600 mm is not a room-depth max)", () => {
    const tooSmall = rule({
      id: "small-depth",
      object: "глубина помещения",
      type: "max_value",
      value: 1600,
      normalized: { max: 1600 },
      source: {
        document: "СП РК 3.02-101",
        clause: "п. 5.2",
        quote: "Глубина не более 1,6 м.",
      },
    });

    assert.equal(pickBestDepthRule([tooSmall]), null);
  });

  it("accepts a genuine room-depth maximum", () => {
    const depth = rule({
      id: "good-depth",
      object: "глубина жилой комнаты",
      type: "max_value",
      value: 6000,
      normalized: { max: 6000 },
      source: {
        document: "СП РК 3.02-101",
        clause: "п. 5.2.4",
        quote: "Глубина жилой комнаты не должна превышать 6 м.",
      },
    });

    assert.equal(pickBestDepthRule([depth])?.id, "good-depth");
  });
});
