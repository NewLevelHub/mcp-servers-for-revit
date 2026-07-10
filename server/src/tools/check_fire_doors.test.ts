import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  formatFireDoorReport,
  type CheckFireDoorsResult,
} from "../tools/check_fire_doors.js";

const sampleResult: CheckFireDoorsResult = {
  success: true,
  message: "Checked 3 doors; 2 require fire rating, 1 non-compliant.",
  mode: "report",
  totalDoors: 3,
  requiredFireDoors: 2,
  nonCompliantCount: 1,
  doors: [
    {
      id: 101,
      uniqueId: "uid-101",
      mark: "D-01",
      family: "Дверь",
      type: "900x2100",
      level: "1 этаж",
      fromRoom: "Коридор",
      toRoom: "Квартира 1",
      openingWidthMm: 900,
      isOnEgressPath: true,
      requiresFireDoor: true,
      ruleId: "between-compartments",
      reason: "Дверь между пожарными отсеками / ограждением преграды",
      source: {
        document: "СН РК 3.02-09-2019",
        clause: "п. 5.3.4",
        quote:
          "Двери в ограждениях противопожарных преград и перегородок, отделяющих пожарные отсеки, должны быть противопожарными.",
      },
      isMarkedAsFireDoor: false,
      currentFireRating: "",
      compliant: false,
    },
    {
      id: 102,
      uniqueId: "uid-102",
      mark: "D-02",
      family: "Дверь EI30",
      type: "900x2100 EI30",
      level: "1 этаж",
      fromRoom: "Лестничная клетка",
      toRoom: "Коридор",
      openingWidthMm: 900,
      isOnEgressPath: true,
      requiresFireDoor: true,
      ruleId: "egress-route",
      reason: "Дверь на пути эвакуации",
      source: {
        document: "СП РК 3.02-109-2012",
        clause: "п. 6.4.12",
        quote:
          "Двери на путях эвакуации, ведущих из помещений на лестничные клетки и в коридоры, должны быть противопожарными.",
      },
      isMarkedAsFireDoor: true,
      currentFireRating: "EI30",
      compliant: true,
    },
    {
      id: 103,
      uniqueId: "uid-103",
      mark: "D-03",
      family: "Дверь",
      type: "800x2100",
      level: "1 этаж",
      fromRoom: "Кухня",
      toRoom: "Гостиная",
      openingWidthMm: 800,
      isOnEgressPath: false,
      requiresFireDoor: false,
      ruleId: "",
      reason: "",
      source: { document: "", clause: "", quote: "" },
      isMarkedAsFireDoor: false,
      currentFireRating: "",
      compliant: true,
    },
  ],
};

describe("formatFireDoorReport", () => {
  it("includes normative citation for required fire doors", () => {
    const report = formatFireDoorReport(sampleResult);

    assert.match(report, /СН РК 3\.02-09-2019/);
    assert.match(report, /п\. 5\.3\.4/);
    assert.match(report, /противопожарными/);
    assert.match(report, /D-01/);
    assert.match(report, /НЕ соответствует/);
    assert.match(report, /Требуют проставления признака/);
  });

  it("reports zero required doors when none match rules", () => {
    const emptyRequired: CheckFireDoorsResult = {
      ...sampleResult,
      requiredFireDoors: 0,
      nonCompliantCount: 0,
      doors: sampleResult.doors.map((door) => ({
        ...door,
        requiresFireDoor: false,
        compliant: true,
      })),
    };

    const report = formatFireDoorReport(emptyRequired);
    assert.match(report, /не требуются/);
  });
});
