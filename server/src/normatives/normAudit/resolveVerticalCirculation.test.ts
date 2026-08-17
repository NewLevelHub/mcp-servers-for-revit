import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { StoredNormRule } from "../rulesStore.js";
import {
  isAccessibilityRule,
  pickBestStairRiserRule,
  pickBestStairTreadRule,
  scoreStairRiserRule,
  scoreStairTreadRule,
} from "./resolveVerticalCirculation.js";

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
    object: partial.object ?? "проступь",
    value: partial.value ?? 0.25,
    unit: partial.unit ?? "m",
    source: partial.source ?? {
      document: "СП РК 3.06-31-2005",
      clause: "п. 9.x",
      quote: "Ширина проступи лестничного марша не менее 0,25 м.",
    },
    applicability: partial.applicability ?? null,
    normalized: partial.normalized,
    ...partial,
  };
}

describe("pickBestStairTreadRule", () => {
  it("rejects bench/seat height from СП РК 3.06-101 п. 4.3.1.22 (false positive)", () => {
    // Real false positive: matcher treated «скамьи … от 0,38 м» as min tread 380 mm.
    const bench = rule({
      id: "bench-seat-height",
      object: "скамья",
      type: "range",
      value: 0.38,
      unit: "m",
      normalized: { min: 380, max: 580 },
      source: {
        document: "СП РК 3.06-101",
        clause: "п. 4.3.1.22",
        quote:
          "В случае примыкания места отдыха к пешеходным путям, расположенным на другом уровне, следует обеспечить плавный переход между этими поверхностями. В местах отдыха следует применять скамьи разной высоты от 0,38 м до 0,58 м с опорой для спины. Сиденья должны иметь не менее одного подлокотника.",
      },
    });

    assert.ok(scoreStairTreadRule(bench) <= 0);
    assert.equal(pickBestStairTreadRule([bench]), null);
  });

  it("accepts real stair tread min and prefers it over bench rule", () => {
    const bench = rule({
      id: "bench",
      object: "скамья",
      normalized: { min: 380 },
      source: {
        document: "СП РК 3.06-101",
        clause: "п. 4.3.1.22",
        quote: "Скамьи высотой от 0,38 м до 0,58 м с опорой для спины.",
      },
    });
    const tread = rule({
      id: "stair-tread",
      object: "ширина проступи",
      type: "min_value",
      value: 0.25,
      unit: "m",
      normalized: { min: 250 },
      source: {
        document: "СП РК 3.06-31-2005",
        clause: "п. 9.15",
        quote: "Ширина проступи лестничного марша должна быть не менее 0,25 м.",
      },
    });

    const picked = pickBestStairTreadRule([bench, tread]);
    assert.ok(picked);
    assert.equal(picked!.id, "stair-tread");
    assert.equal(picked!.normalized?.min, 250);
  });

  it("rejects tread mins above 350 mm (seat-height band)", () => {
    const tooDeep = rule({
      id: "fake-tread-380",
      object: "проступь",
      normalized: { min: 380 },
      source: {
        document: "СП РК 3.06-101",
        clause: "п. x",
        quote: "Проступь лестницы не менее 0,38 м.",
      },
    });
    assert.ok(scoreStairTreadRule(tooDeep) <= 0);
  });
});

describe("МГН scope for stair geometry", () => {
  // Real false positive: -1 этаж of an ordinary house was reported as
  // «проступь 250 < 300 мм · СП РК 3.06-101 п. 4.3.2.27» while the same audit
  // said МГН checks were off. 250 mm meets the general norm.
  const mgnTread = rule({
    id: "mgn-tread-300",
    object: "ширина",
    type: "min_value",
    value: 0.3,
    unit: "m",
    normalized: { exact: 300 },
    source: {
      document: "СП РК 3.06-101",
      clause: "п. 4.3.2.27",
      quote:
        "Ширина лестничных маршей открытых лестниц должна быть не менее 1,35 м. " +
        "Ширина проступей должна быть для наружных лестниц не менее 0,4 м, для внутренних лестниц - не менее 0,3 м.",
    },
  });

  const generalTread = rule({
    id: "general-tread-250",
    object: "ширина проступи",
    type: "min_value",
    value: 0.25,
    unit: "m",
    normalized: { min: 250 },
    source: {
      document: "СП РК 3.02-101-2012",
      clause: "п. 9.15",
      quote: "Ширина проступи лестничного марша должна быть не менее 0,25 м.",
    },
  });

  it("drops МГН tread rules for ordinary housing", () => {
    assert.ok(isAccessibilityRule(mgnTread));
    assert.equal(pickBestStairTreadRule([mgnTread]), null);
  });

  it("prefers the general tread rule over the МГН one", () => {
    const picked = pickBestStairTreadRule([mgnTread, generalTread]);
    assert.equal(picked?.id, "general-tread-250");
  });

  it("applies the МГН rule when accessibility scope is requested", () => {
    const picked = pickBestStairTreadRule([mgnTread], {
      includeAccessibility: true,
    });
    assert.equal(picked?.id, "mgn-tread-300");
  });

  it("keeps a general rule whose quote carries МГН text from PDF extraction", () => {
    // п. 9.7 of SP RK 3.06-31-2005 really does bleed «кресло-коляска» into its
    // quote. Matching on wording instead of document would drop it.
    const evacuationWidth = rule({
      id: "evac-march-1350",
      object: "ширина марша лестницы",
      type: "min_value",
      value: 1.35,
      unit: "m",
      normalized: { exact: 1350 },
      source: {
        document: "SP RK 3.06-31-2005",
        clause: "п. 9.7",
        quote:
          "Ширина марша лестницы, предназначенной для эвакуации, должна быть не менее 1,35 м. " +
          "– велоколяска; 2 – кресло-коляска; 3 – кресло с поручнем для пересадки",
      },
    });
    assert.equal(isAccessibilityRule(evacuationWidth), false);
  });
});

describe("pickBestStairRiserRule", () => {
  it("accepts max riser and rejects bench rules", () => {
    const bench = rule({
      id: "bench-riser-like",
      object: "скамья",
      type: "max_value",
      value: 0.19,
      unit: "m",
      normalized: { max: 190 },
      source: {
        document: "СП РК 3.06-101",
        clause: "п. 4.3.1.22",
        quote: "Скамьи высотой не более 0,19 м.",
      },
    });
    const riser = rule({
      id: "stair-riser",
      object: "высота подступенка",
      type: "max_value",
      value: 0.19,
      unit: "m",
      normalized: { max: 190 },
      source: {
        document: "СП РК 3.06-31-2005",
        clause: "п. 9.15",
        quote: "Высота подступенка лестницы не более 0,19 м.",
      },
    });

    assert.ok(scoreStairRiserRule(bench) <= 0);
    const picked = pickBestStairRiserRule([bench, riser]);
    assert.ok(picked);
    assert.equal(picked!.id, "stair-riser");
  });
});
