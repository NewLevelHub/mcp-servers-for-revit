import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { StoredNormRule } from "../rulesStore.js";
import { pickBestDoorWidthRule, scoreDoorWidthRule } from "./resolveDoorWidth.js";

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
    object: partial.object ?? "дверь",
    value: partial.value ?? 0.9,
    unit: partial.unit ?? "m",
    source: partial.source ?? {
      document: "СП РК 3.02-101-2012",
      clause: "п. 4.6.11",
      quote: "Ширина дверных проёмов не менее 0,9 м.",
    },
    applicability: partial.applicability ?? null,
    normalized: partial.normalized,
    ...partial,
  };
}

describe("pickBestDoorWidthRule", () => {
  it("accepts a genuine egress door-opening minimum (0.9 m)", () => {
    const doorRule = rule({
      id: "door-egress",
      object: "дверь",
      normalized: { exact: 900 },
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.6.11",
        quote:
          "Ширина открытых и дверных проёмов, выхода из помещений и коридоров на лестничную клетку не менее 0,9 м.",
      },
    });

    const picked = pickBestDoorWidthRule([doorRule]);
    assert.ok(picked);
    assert.equal(picked!.id, "door-egress");
  });

  it("rejects a stair-march width (1.35 m) that merely mentions a door", () => {
    const march = rule({
      id: "march",
      object: "дверь",
      value: 1.35,
      normalized: { exact: 1350 },
      source: {
        document: "SP RK 3.06-31-2005",
        clause: "п. 9.7",
        quote:
          "Ширина марша лестницы должна быть не менее ширины любого эвакуационного выхода (двери) на нее, но не менее 1,35 м.",
      },
    });

    assert.equal(scoreDoorWidthRule(march) <= 0, true);
    assert.equal(pickBestDoorWidthRule([march]), null);
  });

  it("rejects an implausibly wide value (2.4 m theatre-hall doors)", () => {
    const hall = rule({
      id: "hall",
      object: "дверь",
      value: 2.4,
      normalized: { exact: 2400 },
      source: {
        document: "СП РК 3.02-107",
        clause: "п. 4.2.2.50",
        quote: "Ширина дверных проёмов в зрительном зале 1,2 – 2,4 м.",
      },
    });

    assert.equal(pickBestDoorWidthRule([hall]), null);
  });

  it("rejects a window-opening rule (not a door)", () => {
    const window = rule({
      id: "window",
      object: "окно",
      normalized: { min: 900 },
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 5.1",
        quote: "Ширина оконного проёма не менее 0,9 м.",
      },
    });

    assert.equal(pickBestDoorWidthRule([window]), null);
  });

  it("prefers the residential-buildings door rule over a school-specific one", () => {
    const residential = rule({
      id: "residential",
      object: "дверь",
      normalized: { exact: 900 },
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.6.11",
        quote: "Ширина дверных проёмов на лестничную клетку не менее 0,9 м.",
      },
    });
    const school = rule({
      id: "school",
      object: "дверь",
      normalized: { exact: 900 },
      source: {
        document: "СП РК 3.02-107",
        clause: "п. 4.2.2.29",
        quote: "Ширина дверей из учебных помещений не менее 0,9 м.",
      },
    });

    const picked = pickBestDoorWidthRule([school, residential]);
    assert.equal(picked?.id, "residential");
  });
});
