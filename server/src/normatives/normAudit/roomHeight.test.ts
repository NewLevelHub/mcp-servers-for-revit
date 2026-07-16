import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { StoredNormRule } from "../rulesStore.js";
import { curatedRoomHeightRule } from "./curatedResidentialRoomNorms.js";
import {
  pickBestRoomHeightRule,
  scoreRoomHeightRule,
} from "./resolveRoomHeightLimit.js";
import { classifyRoomHeights } from "./roomHeight.js";

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
    object: partial.object ?? "высота",
    value: partial.value ?? 2.5,
    unit: partial.unit ?? "m",
    source: partial.source ?? {
      document: "СП РК 3.02-101-2012",
      clause: "табл. 1",
      quote: "Высота жилых помещений от пола до низа потолков 2,5 м",
    },
    applicability: partial.applicability ?? null,
    normalized: partial.normalized,
    ...partial,
  };
}

describe("classifyRoomHeights (golden)", () => {
  it("flags low ceiling in living room", () => {
    const result = classifyRoomHeights(
      [
        { id: 1, name: "Жилая", clearHeightMm: 2400 },
        { id: 2, name: "Кухня", clearHeightMm: 2700 },
      ],
      { minHeightMm: 2500, nearLimitToleranceMm: 50 }
    );

    assert.equal(result.violations.length, 1);
    assert.equal(result.violations[0].id, 1);
    assert.equal(result.compliant.length, 1);
  });
});

describe("pickBestRoomHeightRule", () => {
  it("rejects public bath / laundry 3.6 m for residential", () => {
    const laundry = rule({
      id: "laundry-36",
      value: 3.6,
      normalized: { exact: 3600 },
      source: {
        document: "СП РК 3.02-107",
        clause: "п. 4.1.6",
        quote:
          "монша және кір жуу-химиялық өндірістік үй-жайларының биіктігін - кемінде 3,6 м",
      },
    });
    assert.equal(scoreRoomHeightRule(laundry), -1000);
  });

  it("prefers curated 2.5 m from СП РК 3.02-101", () => {
    const laundry = rule({
      id: "laundry-36",
      value: 3.6,
      normalized: { exact: 3600 },
      source: {
        document: "СП РК 3.02-107",
        clause: "п. 4.1.6",
        quote:
          "Қоғамдық ғимараттар мен шипажайлардың тұрғын үй-жайларының биіктігін кемінде 3 м",
      },
    });
    const curated = {
      ...curatedRoomHeightRule(),
      ruleKey: curatedRoomHeightRule().id,
      documentVersion: "27.04.2021",
      createdAt: 0,
      updatedAt: 0,
      applicability: curatedRoomHeightRule().applicability ?? null,
      tags: curatedRoomHeightRule().tags ?? [],
    } as StoredNormRule;
    const picked = pickBestRoomHeightRule([laundry, curated]);
    assert.equal(picked?.id, curated.id);
  });
});
