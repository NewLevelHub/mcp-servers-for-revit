import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { applyFireDoorRules, detectDoorScenarios } from "./applyFireDoorRules.js";
import {
  extractFireDoorRulesFromText,
  isFireDoorRequirementQuote,
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
    assert.match(rules[0].source.quote, /самозакрывающ|противопожарн/i);
    assert.ok(rules[0].source.document.includes("3.02-101"));
  });

  it("rejects evacuation path-length snippets as fire-door rules", () => {
    const text =
      "Если помещение предназначено для сна, то путь эвакуации по горизонтальному " +
      "проходу от двери этого помещения до защищенного эвакуационного выхода, " +
      "ведущего к лестничной клетке, должен иметь протяженность не более 30 м.";

    assert.equal(isFireDoorRequirementQuote(text), false);
    const rules = extractFireDoorRulesFromText(
      text,
      "СП РК 3.02-109-2012",
      "SP_RK_3.02-109-2012_07.08.2018.pdf"
    );
    assert.equal(rules.length, 0);
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
    assert.ok(
      rules.every((rule) => isFireDoorRequirementQuote(rule.source.quote))
    );
  });
});

describe("applyFireDoorRules", () => {
  it("marks stair-to-corridor door as requiring fire rating with norm citation", () => {
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
          fromRoom: "Лестничная клетка",
          toRoom: "Коридор",
          isOnEgressPath: true,
          isMarkedAsFireDoor: false,
          markSource: "none",
          currentFireRating: "",
          scheduleNote: "",
        },
      ],
      rules
    );

    assert.equal(result.requiredFireDoors, 1);
    assert.equal(result.doors[0].requiresFireDoor, true);
    assert.match(result.doors[0].source.quote, /противопожарн/i);
    assert.equal(result.doors[0].compliant, false);
  });

  it("does not flag apartment entrance (прихожая↔коридор) from path-length quotes", () => {
    const junk = extractFireDoorRulesFromText(
      "Если помещение предназначено для сна, то путь эвакуации по горизонтальному " +
        "проходу от двери этого помещения до защищенного эвакуационного выхода, " +
        "ведущего к лестничной клетке, должен иметь протяженность не более 30 м.",
      "СП РК 3.02-109-2012",
      "SP_RK_3.02-109-2012_07.08.2018.pdf"
    );
    assert.equal(junk.length, 0);

    const realRules = extractFireDoorRulesFromText(
      "5.3.4 Двери в ограждениях противопожарных преград, отделяющих пожарные отсеки, должны быть противопожарными.",
      "СН РК 3.02-09-2019",
      "СН РК_3.02-09-2019.pdf"
    );

    const result = applyFireDoorRules(
      [
        {
          id: 667528,
          uniqueId: "u667528",
          mark: "366",
          family: "Door",
          type: "1050",
          level: "2 этаж",
          fromRoom: "Прихожая 183",
          toRoom: "Межквартирный коридор 167",
          isOnEgressPath: true,
          isMarkedAsFireDoor: false,
          markSource: "none",
          currentFireRating: "",
          scheduleNote: "",
        },
      ],
      realRules
    );

    assert.equal(result.doors[0].requiresFireDoor, false);
    assert.equal(result.requiredFireDoors, 0);
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
      markSource: "none",
      currentFireRating: "",
      scheduleNote: "",
    });

    assert.ok(scenarios.includes("stair-to-corridor"));
  });

  it("treats schedule_note mark as compliant when fire door required", () => {
    const rules = extractFireDoorRulesFromText(
      "5.3.4 Двери в ограждениях противопожарных преград, отделяющих пожарные отсеки, должны быть противопожарными.",
      "СН РК 3.02-09-2019",
      "СН РК_3.02-09-2019.pdf"
    );

    const result = applyFireDoorRules(
      [
        {
          id: 3109,
          uniqueId: "u3109",
          mark: "3109",
          family: "АС_Дверь_Двупольная_Стальная1",
          type: "(дверь)ДОмп_Л_2100-1200",
          level: "1",
          fromRoom: "Лифтовый холл",
          toRoom: "ПУИ",
          isOnEgressPath: true,
          isMarkedAsFireDoor: true,
          markSource: "schedule_note",
          currentFireRating: "EI30",
          scheduleNote:
            "Дверь остекленная, металлическая, противопожарная EI 30, с порогом.",
        },
      ],
      rules
    );

    assert.equal(result.requiredFireDoors, 1);
    assert.equal(result.doors[0].compliant, true);
    assert.equal(result.doors[0].markSource, "schedule_note");
  });
});
