import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { applyFireDoorRules, detectDoorScenarios } from "./applyFireDoorRules.js";
import {
  extractFireDoorRulesFromText,
  loadFireDoorRulesFromNormatives,
  normalizeDocumentName,
  resolveNormativesDir,
} from "./fireDoorRules.js";

describe("fireDoorRules", () => {
  it("normalizes SP RK file names to document codes", () => {
    assert.equal(
      normalizeDocumentName("SP_RK_3.02-101-2012_27.04.2021.pdf"),
      "СП РК 3.02-101-2012"
    );
  });

  it("extracts fire door requirement sentences with clause", () => {
    const text =
      "4.2.22 Двери эвакуационных выходов должны открываться по направлению выхода. " +
      "На пути от квартиры до лестничной клетки Н1 должно быть не менее двух самозакрывающихся дверей.";

    const rules = extractFireDoorRulesFromText(
      text,
      "СП РК 3.02-101-2012",
      "SP_RK_3.02-101-2012_27.04.2021.pdf"
    );

    assert.ok(rules.length >= 1);
    assert.match(rules[0].source.quote, /двер/i);
    assert.ok(rules[0].source.document.includes("3.02-101"));
  });

  it("loads rules from repo normatives directory", async () => {
    const normativesDir = await resolveNormativesDir();
    const { rules, normativesDir: resolvedDir } =
      await loadFireDoorRulesFromNormatives({
        normativesDir,
        pdfFiles: ["SP_RK_3.02-101-2012_27.04.2021.pdf"],
      });

    assert.equal(resolvedDir, normativesDir);
    assert.ok(rules.length > 0);
    assert.ok(rules.some((rule) => /двер/i.test(rule.source.quote)));
  });
});

describe("applyFireDoorRules", () => {
  it("marks corridor-to-apartment door as requiring fire rating with norm citation", () => {
    const rules = extractFireDoorRulesFromText(
      "5.3.4 Двери в ограждениях противопожарных преград, отделяющих пожарные отсеки, должны быть противопожарными.",
      "СН РК 3.02-09-2019",
      "СН РК_3.02-09-2019.pdf"
    );

    const result = applyFireDoorRules(
      [
        {
          id: 1,
          uniqueId: "u1",
          mark: "D-1",
          family: "Door",
          type: "900",
          level: "1",
          fromRoom: "Коридор",
          toRoom: "Квартира 1",
          isOnEgressPath: true,
          isMarkedAsFireDoor: false,
          currentFireRating: "",
        },
      ],
      rules
    );

    assert.equal(result.requiredFireDoors, 1);
    assert.equal(result.doors[0].requiresFireDoor, true);
    assert.match(result.doors[0].source.quote, /противопожарн/i);
    assert.equal(result.doors[0].compliant, false);
  });

  it("detects stair-to-corridor scenario", () => {
    const scenarios = detectDoorScenarios({
      id: 2,
      uniqueId: "u2",
      mark: "",
      family: "",
      type: "",
      level: "",
      fromRoom: "Лестничная клетка",
      toRoom: "Коридор",
      isOnEgressPath: true,
      isMarkedAsFireDoor: false,
      currentFireRating: "",
    });

    assert.ok(scenarios.includes("stair-to-corridor"));
  });
});
