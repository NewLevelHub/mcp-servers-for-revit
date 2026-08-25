import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  evaluateStandard,
  summarizeFindings,
  type RawModelFacts,
  type StandardConfig,
} from "./standardRules.js";

/** Empty facts, easy to extend per test — a real payload has all of these keys. */
function emptyFacts(): RawModelFacts {
  return {
    worksharingEnabled: false,
    worksets: [],
    types: [],
    elementsWithoutLevel: [],
    worksetByCategory: [],
    groups: [],
    views: [],
    links: [],
  };
}

describe("evaluateStandard — defaults (no config)", () => {
  it("flags nothing about names without a configured pattern", () => {
    const facts = emptyFacts();
    facts.types.push({
      category: "OST_Doors",
      familyName: "Дверь одностворчатая",
      typeName: "some_weird_NAME 900x2100",
      typeId: 1,
      instanceCount: 3,
    });
    const findings = evaluateStandard(facts);
    assert.equal(findings.some((f) => f.category === "naming"), false);
  });

  it("still runs structural checks with the built-in default config", () => {
    const facts = emptyFacts();
    facts.elementsWithoutLevel.push({ category: "OST_Walls", count: 2, sampleElementIds: [10, 11] });
    const findings = evaluateStandard(facts);
    assert.equal(findings.some((f) => f.category === "level"), true);
  });
});

describe("evaluateStandard — naming patterns", () => {
  it("flags a type name that does not match the category pattern", () => {
    const facts = emptyFacts();
    facts.types.push({
      category: "OST_Doors",
      familyName: "F",
      typeName: "bad name",
      typeId: 1,
      instanceCount: 1,
    });
    const config: StandardConfig = { typeNamePattern: { OST_Doors: "^ДВ-\\d+$" } };
    const findings = evaluateStandard(facts, config);
    assert.equal(findings.length, 1);
    assert.equal(findings[0].severity, "fix");
    assert.equal(findings[0].category, "naming");
  });

  it("does not flag a matching type name", () => {
    const facts = emptyFacts();
    facts.types.push({ category: "OST_Doors", familyName: "F", typeName: "ДВ-01", typeId: 1, instanceCount: 1 });
    const config: StandardConfig = { typeNamePattern: { OST_Doors: "^ДВ-\\d+$" } };
    assert.equal(evaluateStandard(facts, config).length, 0);
  });

  it("falls back to the wildcard pattern for a category with no entry of its own", () => {
    const facts = emptyFacts();
    facts.types.push({ category: "OST_Windows", familyName: "F", typeName: "нет по шаблону", typeId: 1, instanceCount: 1 });
    const config: StandardConfig = { typeNamePattern: { "*": "^ОК-\\d+$" } };
    const findings = evaluateStandard(facts, config);
    assert.equal(findings.length, 1);
  });

  it("skips family-name checks on system types (no family at all)", () => {
    const facts = emptyFacts();
    facts.types.push({ category: "OST_Walls", familyName: "", typeName: "Стена 200", typeId: 1, instanceCount: 1 });
    const config: StandardConfig = { familyNamePattern: { "*": "^F-\\d+$" } };
    assert.equal(evaluateStandard(facts, config).length, 0);
  });

  it("a broken regex in the config is skipped, not thrown", () => {
    const facts = emptyFacts();
    facts.types.push({ category: "OST_Doors", familyName: "F", typeName: "x", typeId: 1, instanceCount: 1 });
    const config: StandardConfig = { typeNamePattern: { OST_Doors: "(unterminated" } };
    assert.doesNotThrow(() => evaluateStandard(facts, config));
  });
});

describe("evaluateStandard — elements without a level", () => {
  it("is critical severity and carries the sample ids", () => {
    const facts = emptyFacts();
    facts.elementsWithoutLevel.push({ category: "OST_Furniture", count: 5, sampleElementIds: [1, 2, 3] });
    const findings = evaluateStandard(facts);
    assert.equal(findings.length, 1);
    assert.equal(findings[0].severity, "critical");
    assert.deepEqual(findings[0].elementIds, [1, 2, 3]);
  });

  it("can be turned off", () => {
    const facts = emptyFacts();
    facts.elementsWithoutLevel.push({ category: "OST_Furniture", count: 5, sampleElementIds: [] });
    assert.equal(evaluateStandard(facts, { flagElementsWithoutLevel: false }).length, 0);
  });
});

describe("evaluateStandard — workset outliers", () => {
  it("flags the minority workset, not the majority", () => {
    const facts = emptyFacts();
    facts.worksharingEnabled = true;
    facts.worksetByCategory.push(
      { category: "OST_Walls", worksetName: "Архитектура", count: 400, sampleElementIds: [1] },
      { category: "OST_Walls", worksetName: "Workset1", count: 3, sampleElementIds: [2, 3] }
    );
    const findings = evaluateStandard(facts);
    assert.equal(findings.length, 1);
    // The flagged (minority) workset is Workset1; Архитектура only appears as the
    // "majority is over there" reference later in the same message.
    assert.match(findings[0].message, /лежат в ворксете «Workset1»/);
    assert.match(findings[0].message, /большинство \(400\) — в «Архитектура»/);
  });

  it("does nothing when a category has only one workset", () => {
    const facts = emptyFacts();
    facts.worksharingEnabled = true;
    facts.worksetByCategory.push({ category: "OST_Walls", worksetName: "Архитектура", count: 400, sampleElementIds: [] });
    assert.equal(evaluateStandard(facts).length, 0);
  });

  it("is skipped entirely when worksharing is off", () => {
    const facts = emptyFacts();
    facts.worksharingEnabled = false;
    facts.worksetByCategory.push(
      { category: "OST_Walls", worksetName: "A", count: 400, sampleElementIds: [] },
      { category: "OST_Walls", worksetName: "B", count: 3, sampleElementIds: [] }
    );
    assert.equal(evaluateStandard(facts).length, 0);
  });
});

describe("evaluateStandard — duplicate type names", () => {
  it("flags the Revit conflict-rename pattern: base name plus a (2)/(3) sibling", () => {
    const facts = emptyFacts();
    facts.types.push(
      { category: "OST_Doors", familyName: "F", typeName: "Дверь 900", typeId: 1, instanceCount: 1 },
      { category: "OST_Doors", familyName: "F", typeName: "Дверь 900(2)", typeId: 2, instanceCount: 1 }
    );
    const findings = evaluateStandard(facts);
    assert.equal(findings.filter((f) => f.category === "duplicate-type").length, 1);
  });

  it("does NOT flag the same name repeated with no (2)/(3) marker (REV-179, caught live)", () => {
    // Measured on a real model: stair run/landing types get one auto-named instance-type per
    // stair, identically named, on purpose — not a mistake to report.
    const facts = emptyFacts();
    facts.types.push(
      { category: "OST_StairsRuns", familyName: "F", typeName: "Марш(Внутренний)", typeId: 1, instanceCount: 1 },
      { category: "OST_StairsRuns", familyName: "F", typeName: "Марш(Внутренний)", typeId: 2, instanceCount: 1 }
    );
    assert.equal(evaluateStandard(facts).length, 0);
  });

  it("does NOT flag the same name in different families (REV-179, caught live)", () => {
    // A DWG block and an unrelated door family can coincidentally share a dimensional name.
    const facts = emptyFacts();
    facts.types.push(
      { category: "OST_Doors", familyName: "Семейство А", typeName: "900", typeId: 1, instanceCount: 1 },
      { category: "OST_Doors", familyName: "Семейство Б", typeName: "900", typeId: 2, instanceCount: 1 }
    );
    assert.equal(evaluateStandard(facts).length, 0);
  });

  it("does not flag the same type counted once (single typeId)", () => {
    const facts = emptyFacts();
    facts.types.push({ category: "OST_Doors", familyName: "F", typeName: "Дверь 900", typeId: 1, instanceCount: 5 });
    assert.equal(evaluateStandard(facts).length, 0);
  });

  it("does not cross categories", () => {
    const facts = emptyFacts();
    facts.types.push(
      { category: "OST_Doors", familyName: "F", typeName: "900", typeId: 1, instanceCount: 1 },
      { category: "OST_Windows", familyName: "F", typeName: "900(2)", typeId: 2, instanceCount: 1 }
    );
    assert.equal(evaluateStandard(facts).length, 0);
  });
});

describe("evaluateStandard — unused types", () => {
  it("flags a loaded type with zero instances as optional", () => {
    const facts = emptyFacts();
    facts.types.push({ category: "OST_Doors", familyName: "F", typeName: "x", typeId: 1, instanceCount: 0 });
    const findings = evaluateStandard(facts);
    assert.equal(findings.length, 1);
    assert.equal(findings[0].severity, "optional");
    assert.equal(findings[0].category, "unused-type");
  });

  it("does not flag a placed type", () => {
    const facts = emptyFacts();
    facts.types.push({ category: "OST_Doors", familyName: "F", typeName: "x", typeId: 1, instanceCount: 1 });
    assert.equal(evaluateStandard(facts).length, 0);
  });
});

describe("evaluateStandard — groups", () => {
  it("flags an empty group as fix severity", () => {
    const facts = emptyFacts();
    facts.groups.push({ name: "G1", kind: "Model", instanceCount: 2, memberCount: 0 });
    const findings = evaluateStandard(facts);
    assert.equal(findings.length, 1);
    assert.equal(findings[0].severity, "fix");
  });

  it("flags instance count over the configured ceiling as optional", () => {
    const facts = emptyFacts();
    facts.groups.push({ name: "G1", kind: "Model", instanceCount: 50, memberCount: 3 });
    const findings = evaluateStandard(facts, { maxGroupInstances: 10 });
    assert.equal(findings.length, 1);
    assert.equal(findings[0].severity, "optional");
  });

  it("no ceiling configured means no instance-count finding", () => {
    const facts = emptyFacts();
    facts.groups.push({ name: "G1", kind: "Model", instanceCount: 500, memberCount: 3 });
    assert.equal(evaluateStandard(facts).length, 0);
  });
});

describe("evaluateStandard — views", () => {
  it("is off by default", () => {
    const facts = emptyFacts();
    facts.views.push({ name: "План 1", viewType: "FloorPlan", hasTemplate: false });
    assert.equal(evaluateStandard(facts).length, 0);
  });

  it("flags a template-less view when turned on", () => {
    const facts = emptyFacts();
    facts.views.push({ name: "План 1", viewType: "FloorPlan", hasTemplate: false, scale: 100 });
    const findings = evaluateStandard(facts, { flagViewsWithoutTemplate: true });
    assert.equal(findings.length, 1);
    assert.equal(findings[0].severity, "optional");
  });

  it("does not flag a view that has a template", () => {
    const facts = emptyFacts();
    facts.views.push({ name: "План 1", viewType: "FloorPlan", hasTemplate: true, templateName: "АР_План" });
    assert.equal(evaluateStandard(facts, { flagViewsWithoutTemplate: true }).length, 0);
  });
});

describe("evaluateStandard — links", () => {
  it("NotFound/Invalid are critical", () => {
    const facts = emptyFacts();
    facts.links.push({ name: "КР.rvt", status: "NotFound" });
    const findings = evaluateStandard(facts);
    assert.equal(findings[0].severity, "critical");
  });

  it("Unloaded is only optional — often deliberate", () => {
    const facts = emptyFacts();
    facts.links.push({ name: "ИОС.rvt", status: "Unloaded" });
    const findings = evaluateStandard(facts);
    assert.equal(findings[0].severity, "optional");
  });

  it("Loaded is not a finding", () => {
    const facts = emptyFacts();
    facts.links.push({ name: "КР.rvt", status: "Loaded" });
    assert.equal(evaluateStandard(facts).length, 0);
  });
});

describe("evaluateStandard — ordering and summary", () => {
  it("sorts critical, then fix, then optional", () => {
    const facts = emptyFacts();
    facts.types.push({ category: "OST_Doors", familyName: "F", typeName: "unused", typeId: 1, instanceCount: 0 }); // optional
    facts.groups.push({ name: "G", kind: "Model", instanceCount: 1, memberCount: 0 }); // fix
    facts.elementsWithoutLevel.push({ category: "OST_Walls", count: 1, sampleElementIds: [] }); // critical

    const findings = evaluateStandard(facts);
    assert.deepEqual(
      findings.map((f) => f.severity),
      ["critical", "fix", "optional"]
    );
  });

  it("summarizeFindings counts each severity", () => {
    const findings = evaluateStandard(
      (() => {
        const facts = emptyFacts();
        facts.elementsWithoutLevel.push({ category: "OST_Walls", count: 1, sampleElementIds: [] });
        facts.groups.push({ name: "G", kind: "Model", instanceCount: 1, memberCount: 0 });
        facts.types.push({ category: "OST_Doors", familyName: "F", typeName: "unused", typeId: 1, instanceCount: 0 });
        return facts;
      })()
    );
    assert.deepEqual(summarizeFindings(findings), { critical: 1, fix: 1, optional: 1 });
  });

  it("an entirely clean model produces no findings", () => {
    assert.equal(evaluateStandard(emptyFacts()).length, 0);
  });
});
