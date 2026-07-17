import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { StoredNormRule } from "../rulesStore.js";
import {
  curatedBathroomAreaRule,
  curatedRulesAsStored,
} from "./curatedResidentialRoomNorms.js";
import { parseAreaMinM2FromRule } from "./normAreaParsing.js";
import {
  pickBestRoomAreaRule,
  scoreRoomAreaRule,
} from "./resolveRoomAreaLimits.js";

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
    object: partial.object ?? "комната",
    value: partial.value ?? 9,
    unit: partial.unit ?? "m",
    source: partial.source ?? {
      document: "СП РК 3.02-101-2012",
      clause: "п. 5.1.2",
      quote:
        "Площадь жилого помещения (комнаты) в квартире должна быть не менее 9 м².",
    },
    applicability: partial.applicability ?? null,
    normalized: partial.normalized,
    ...partial,
  };
}

describe("pickBestRoomAreaRule", () => {
  it("accepts living room 9 m² rule", () => {
    const living = rule({
      id: "living-9",
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 5.1.2",
        quote:
          "Площадь жилого помещения (комнаты) в квартире должна быть не менее 9 м².",
      },
    });
    assert.ok(scoreRoomAreaRule(living, "living_room") > 0);
    const picked = pickBestRoomAreaRule([living], "living_room");
    assert.equal(picked?.id, "living-9");
  });

  it("rejects tambour area rule for living room", () => {
    const tambour = rule({
      id: "tambour",
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4.10.6",
        quote: "тамбур кемінде 1,65 м × 1,65 м",
      },
      normalized: { min: 1650 },
    });
    assert.equal(scoreRoomAreaRule(tambour, "living_room"), -1000);
  });

  it("rejects kitchen-niche 5 m² as bathroom limit", () => {
    const kitchenNiche = rule({
      id: "kitchen-niche-5",
      value: 5,
      unit: "m",
      source: {
        document: "SP RK 3.06-31-2005",
        clause: "п. 5.10",
        quote:
          "допускается предусматривать кухню-нишу площадью не менее 5 м 2.",
      },
      normalized: { exact: 5000 },
    });
    assert.equal(scoreRoomAreaRule(kitchenNiche, "bathroom"), -1000);
  });

  it("prefers curated bathroom 2.25 m² over kitchen rules", () => {
    const kitchen = rule({
      id: "kitchen-8",
      value: 8,
      source: {
        document: "SP RK 3.06-31-2005",
        clause: "п. 5.10",
        quote: "Площадь кухни рекомендуется принимать не менее 8 м 2.",
      },
    });
    const curated = curatedBathroomAreaRule();
    const bathroom = {
      ...curated,
      ruleKey: curated.id,
      documentVersion: "27.04.2021",
      createdAt: 0,
      updatedAt: 0,
      applicability: curated.applicability ?? null,
      tags: curated.tags ?? [],
    } as StoredNormRule;
    const picked = pickBestRoomAreaRule([kitchen, bathroom], "bathroom");
    assert.equal(picked?.id, bathroom.id);
    assert.equal(parseAreaMinM2FromRule(bathroom), 2.25);
  });
});

describe("parseAreaMinM2FromRule bathroom note", () => {
  it("parses ванной - 2,25 м²", () => {
    const bathroom = curatedRulesAsStored().find((r) =>
      r.id.includes("ванная")
    )!;
    assert.equal(parseAreaMinM2FromRule(bathroom), 2.25);
  });
});
