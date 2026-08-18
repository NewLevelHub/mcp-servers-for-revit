import test from "node:test";
import assert from "node:assert/strict";
import {
  analyzeDocumentCoverage,
  coverageBand,
  describeCoverage,
  looksLikeRequirement,
  normalizeClause,
} from "./coverage.js";

const doc = (clauses: string[]) => clauses.join("\n");

test("requirement wording is what makes a clause countable", () => {
  assert.ok(looksLikeRequirement("Ширина должна быть не менее 1,2 м"));
  assert.ok(looksLikeRequirement("Не допускается размещение котельных"));
  assert.ok(looksLikeRequirement("Следует принимать по таблице 3"));
  assert.ok(looksLikeRequirement("Есіктің ені 0,9 м болуы тиіс"), "казахский тоже");
});

test("a definition is not a requirement", () => {
  // Counting these would inflate the denominator and hide real gaps.
  assert.equal(looksLikeRequirement("Настоящий свод правил распространяется на здания"), false);
  assert.equal(looksLikeRequirement("Лоджия — перекрытое пространство"), false);
});

test("clause numbers are compared in one form", () => {
  assert.equal(normalizeClause("п. 4.3.2.12"), "4.3.2.12");
  assert.equal(normalizeClause("пункт 5.1"), "5.1");
  assert.equal(normalizeClause("4.3.2.12."), "4.3.2.12");
  assert.equal(normalizeClause("  4.1  "), "4.1");
});

test("coverage counts requirement clauses, not every clause", () => {
  const text = doc([
    "1.1 Настоящий свод правил распространяется на жилые здания.",
    "1.2 Ширина коридора должна быть не менее 1,4 м.",
    "1.3 Высота помещения должна быть не менее 2,5 м.",
  ]);

  const c = analyzeDocumentCoverage({
    document: "СП тест",
    text,
    storedClauses: ["1.2"],
    rulesInLibrary: 1,
  });

  assert.equal(c.requirementClauses, 2, "1.1 — определение, не требование");
  assert.equal(c.coveredClauses, 1);
  assert.equal(c.coveragePercent, 50);
});

test("a missed requirement is listed so it can be read", () => {
  const c = analyzeDocumentCoverage({
    document: "СП тест",
    text: doc(["2.1 Глубина зоны должна быть не менее 1,5 м."]),
    storedClauses: [],
    rulesInLibrary: 0,
  });

  assert.equal(c.missedSamples.length, 1);
  assert.equal(c.missedSamples[0].clause, "2.1");
  assert.match(c.missedSamples[0].excerpt, /не менее 1,5 м/);
  assert.equal(c.missedSamples[0].hasNumber, true);
});

test("misses carrying numbers are listed first", () => {
  // Those are the ones the extractor was built for, so they are the likelier bug.
  const c = analyzeDocumentCoverage({
    document: "СП тест",
    text: doc([
      "3.1 Не допускается размещение мусоросборников под жилыми комнатами.",
      "3.2 Ширина марша должна быть не менее 1,05 м.",
    ]),
    storedClauses: [],
    rulesInLibrary: 0,
  });

  assert.equal(c.missedSamples[0].hasNumber, true);
  assert.match(c.missedSamples[0].excerpt, /1,05/);
});

test("stored clauses match regardless of how they are written", () => {
  const c = analyzeDocumentCoverage({
    document: "СП тест",
    text: doc(["4.3.2.12 Глубина должна быть не менее 1,5 м."]),
    storedClauses: ["п. 4.3.2.12"],
    rulesInLibrary: 1,
  });

  assert.equal(c.coveredClauses, 1);
  assert.equal(c.coveragePercent, 100);
});

test("a document with no requirements reads as fully covered, not as zero", () => {
  // 0/0 is not a gap. Reporting 0% here would send someone hunting nothing.
  const c = analyzeDocumentCoverage({
    document: "Титульный лист",
    text: doc(["1.1 Настоящий документ введён впервые."]),
    storedClauses: [],
    rulesInLibrary: 0,
  });

  assert.equal(c.requirementClauses, 0);
  assert.equal(c.coveragePercent, 100);
  assert.deepEqual(c.missedSamples, []);
});

test("the sample list is capped so a bad document does not flood the report", () => {
  const text = doc(
    Array.from({ length: 40 }, (_, i) => `5.${i + 1} Размер должен быть не менее ${i + 1} м.`)
  );
  const c = analyzeDocumentCoverage({
    document: "СП тест",
    text,
    storedClauses: [],
    rulesInLibrary: 0,
    maxSamples: 3,
  });

  assert.equal(c.requirementClauses, 40);
  assert.equal(c.missedSamples.length, 3);
});

test("a document that never split into clauses is flagged separately", () => {
  // The fire-safety regulation: 145 pages, 5 requirement clauses found. That is a
  // parse failure, and calling it "0% coverage" would send someone to fix the
  // extractor patterns instead of the PDF's text layer.
  const c = analyzeDocumentCoverage({
    document: "ТР пожарной безопасности",
    text: doc(["Общие требования должны соблюдаться."]),
    storedClauses: [],
    rulesInLibrary: 0,
    pages: 145,
  });

  assert.equal(c.structureSuspect, true);
  assert.match(describeCoverage(c), /не разобрался на пункты/);
  assert.match(describeCoverage(c), /текстовый слой/);
});

test("a document that did split is not flagged, however low its coverage", () => {
  const text = doc(
    Array.from({ length: 30 }, (_, i) => `6.${i + 1} Размер должен быть не менее ${i} м.`)
  );
  const c = analyzeDocumentCoverage({
    document: "СП тест",
    text,
    storedClauses: [],
    rulesInLibrary: 0,
    pages: 20,
  });

  assert.equal(c.structureSuspect, false, "30 требований на 20 страниц — разбор работает");
  assert.equal(c.coveragePercent, 0);
  assert.match(describeCoverage(c), /почти не разобран/);
});

test("without a page count the structure check stays off", () => {
  const c = analyzeDocumentCoverage({
    document: "СП тест",
    text: doc(["7.1 Должно быть не менее 1 м."]),
    storedClauses: [],
    rulesInLibrary: 0,
  });
  assert.equal(c.structureSuspect, false);
});

test("bands split good, partial and thin", () => {
  assert.equal(coverageBand(100), "good");
  assert.equal(coverageBand(70), "good");
  assert.equal(coverageBand(69), "partial");
  assert.equal(coverageBand(30), "partial");
  assert.equal(coverageBand(29), "thin");
  assert.equal(coverageBand(0), "thin");
});

test("a thin document is described as unusable for checking, not merely low", () => {
  // The consequence has to be spelled out: a check with no rules behind it
  // answers "нарушений не найдено", which reads like a pass.
  const text = describeCoverage({
    document: "ТР пожарной безопасности",
    totalClauses: 400,
    requirementClauses: 200,
    coveredClauses: 6,
    coveragePercent: 3,
    rulesInLibrary: 6,
    missedSamples: [],
    structureSuspect: false,
  });

  assert.match(text, /почти не разобран/);
  assert.match(text, /«нарушений не найдено» здесь ничего не значит/);
});

test("a partly covered document warns that silence is not proof", () => {
  const text = describeCoverage({
    document: "СП РК 3.02-107",
    totalClauses: 500,
    requirementClauses: 300,
    coveredClauses: 150,
    coveragePercent: 50,
    rulesInLibrary: 138,
    missedSamples: [],
    structureSuspect: false,
  });

  assert.match(text, /не доказывает соответствие/);
});

test("a well covered document gets no warning tail", () => {
  const text = describeCoverage({
    document: "СН РК 3.02-08",
    totalClauses: 200,
    requirementClauses: 150,
    coveredClauses: 140,
    coveragePercent: 93,
    rulesInLibrary: 145,
    missedSamples: [],
    structureSuspect: false,
  });

  assert.match(text, /93%/);
  assert.doesNotMatch(text, /не значит|не доказывает/);
});
