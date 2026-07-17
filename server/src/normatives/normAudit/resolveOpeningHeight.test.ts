import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { StoredNormRule } from "../rulesStore.js";
import {
  pickBestOpeningHeightRule,
  scoreOpeningHeightRule,
} from "./resolveOpeningHeight.js";

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
    object: partial.object ?? "коридор",
    value: partial.value ?? 1.9,
    unit: partial.unit ?? "m",
    source: partial.source ?? {
      document: "SP RK 3.06-31-2005",
      clause: "п. 9.14",
      quote:
        "Высота эвакуационных выходов в свету должна быть не менее 1,9 м.",
    },
    applicability: partial.applicability ?? null,
    normalized: partial.normalized ?? { exact: 1900 },
    ...partial,
  };
}

describe("scoreOpeningHeightRule", () => {
  it("scores the 1.9 m egress exit height rule highly", () => {
    const scored = scoreOpeningHeightRule(rule({ id: "egress-1900" }));
    assert.ok(scored > 0);
  });

  it("rejects path height that is not an exit opening", () => {
    const scored = scoreOpeningHeightRule(
      rule({
        id: "path-2000",
        value: 2,
        normalized: { exact: 2000 },
        source: {
          document: "СП РК 3.02-101-2012",
          clause: "п. 4.2.20",
          quote:
            "Высота эвакуационных путей в здании должна быть не менее 2 м с учетом установки разбрызгивателей.",
        },
      })
    );
    assert.ok(scored <= 0);
  });

  it("rejects threshold / sill-sized values", () => {
    const scored = scoreOpeningHeightRule(
      rule({
        id: "threshold",
        value: 0.014,
        unit: "m",
        normalized: { exact: 14 },
        source: {
          document: "СП РК 3.02-101-2012",
          clause: "п. 4.6.6",
          quote: "высота порогов должна быть не более 0,014 м",
        },
      })
    );
    assert.ok(scored <= 0);
  });
});

describe("pickBestOpeningHeightRule", () => {
  it("picks the 1900 mm egress exit rule", () => {
    const best = pickBestOpeningHeightRule([
      rule({
        id: "noise",
        value: 2,
        normalized: { exact: 2000 },
        source: {
          document: "СП РК 3.02-101-2012",
          clause: "п. 4.2.20",
          quote: "Высота эвакуационных путей в здании должна быть не менее 2 м.",
        },
      }),
      rule({ id: "egress-1900" }),
    ]);
    assert.equal(best?.id, "egress-1900");
  });
});
