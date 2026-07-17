import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { StoredNormRule } from "../rulesStore.js";
import { parseAreaMinM2FromRule } from "./normAreaParsing.js";

describe("parseAreaMinM2FromRule", () => {
  it("parses kitchen 8 m² from quote when normalized is wrong", () => {
    const rule: StoredNormRule = {
      id: "kitchen",
      ruleKey: "kitchen",
      tags: [],
      documentVersion: null,
      createdAt: 0,
      updatedAt: 0,
      type: "min_value",
      object: "комната",
      value: 5,
      unit: "m",
      source: {
        document: "SP RK 3.06-31-2005",
        clause: "п. 5.10",
        quote:
          "Площадь кухни в однокомнатных квартирах не менее 8 м 2, в двухкомнатных — не менее 9м 2.",
      },
      applicability: null,
      normalized: { exact: 5000 },
    };
    const area = parseAreaMinM2FromRule(rule);
    assert.ok(area != null);
    assert.equal(area, 8);
  });
});
