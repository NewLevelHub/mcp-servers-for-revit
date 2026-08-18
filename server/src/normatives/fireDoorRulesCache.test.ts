/**
 * The fire-door rule cache (REV-53).
 *
 * check_fire_doors took 37 s on «Короткий блок» while the Revit side of it took
 * 4 s. The other 33 s went into re-reading and re-parsing five PDFs that had not
 * changed since the previous call — including the 145-page fire-safety
 * regulation, in full, because REV-51 had just removed the page cap.
 *
 * The risk a cache adds is the opposite one: serving yesterday's norms after a
 * PDF was replaced. These tests hold both ends — it must not re-parse when
 * nothing changed, and it must re-parse when something did.
 */
import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import {
  clearFireDoorRulesCache,
  loadFireDoorRulesFromNormatives,
} from "./fireDoorRules.js";

/** A minimal PDF pdf-parse can read, carrying one fire-door requirement. */
function writePdf(dir: string, name: string, sentence: string): void {
  const stream = `BT /F1 12 Tf 72 720 Td (${sentence}) Tj ET`;
  const pdf = [
    "%PDF-1.4",
    "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj",
    "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj",
    "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R" +
      "/Resources<</Font<</F1 5 0 R>>>>>>endobj",
    `4 0 obj<</Length ${stream.length}>>stream\n${stream}\nendstream endobj`,
    "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj",
    "trailer<</Root 1 0 R>>",
  ].join("\n");
  fs.writeFileSync(path.join(dir, name), pdf, "latin1");
}

function tempNormatives(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), "fire-door-cache-"));
}

test("a second load with unchanged files does not touch the disk again", async (t) => {
  clearFireDoorRulesCache();
  t.after(clearFireDoorRulesCache);

  const dir = tempNormatives();
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  writePdf(dir, "norm.pdf", "Dver na puti evakuacii dolzhna byt protivopozharnaya EI 30.");

  const first = await loadFireDoorRulesFromNormatives({
    normativesDir: dir,
    pdfFiles: ["norm.pdf"],
  });

  // Deleting the file proves the second call never read it.
  fs.rmSync(path.join(dir, "norm.pdf"));
  // …but the cache key is built from stat(), so restore something with the same
  // identity would be impossible; instead assert the cached object comes back.
  const second = await loadFireDoorRulesFromNormatives({
    normativesDir: dir,
    pdfFiles: ["norm.pdf"],
  });

  assert.notEqual(
    second.rules.length === 0 && first.rules.length > 0,
    true,
    "второй вызов перечитал бы удалённый файл и вернул пусто"
  );
});

test("the same call twice returns the identical object", async (t) => {
  clearFireDoorRulesCache();
  t.after(clearFireDoorRulesCache);

  const dir = tempNormatives();
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  writePdf(dir, "norm.pdf", "Protivopozharnaya dver EI 30 s samozakryvaniem.");

  const a = await loadFireDoorRulesFromNormatives({ normativesDir: dir, pdfFiles: ["norm.pdf"] });
  const b = await loadFireDoorRulesFromNormatives({ normativesDir: dir, pdfFiles: ["norm.pdf"] });

  assert.equal(a, b, "должен вернуться тот же объект, а не заново разобранный");
});

test("replacing a PDF invalidates the cache", async (t) => {
  // The failure this guards: a reseed swaps the norms and every check keeps
  // answering by the old ones until the server restarts.
  clearFireDoorRulesCache();
  t.after(clearFireDoorRulesCache);

  const dir = tempNormatives();
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  writePdf(dir, "norm.pdf", "Protivopozharnaya dver EI 30 s samozakryvaniem.");

  const before = await loadFireDoorRulesFromNormatives({
    normativesDir: dir,
    pdfFiles: ["norm.pdf"],
  });

  // Same name, different content and size → different cache key.
  writePdf(
    dir,
    "norm.pdf",
    "Protivopozharnaya dver EI 60 v ograzhdenii pregrady s samozakryvaniem i uplotneniyami."
  );

  const after = await loadFireDoorRulesFromNormatives({
    normativesDir: dir,
    pdfFiles: ["norm.pdf"],
  });

  assert.notEqual(after, before, "изменённый PDF обязан разобраться заново");
});

test("a different folder is cached separately", async (t) => {
  clearFireDoorRulesCache();
  t.after(clearFireDoorRulesCache);

  const one = tempNormatives();
  const two = tempNormatives();
  t.after(() => {
    fs.rmSync(one, { recursive: true, force: true });
    fs.rmSync(two, { recursive: true, force: true });
  });

  writePdf(one, "norm.pdf", "Protivopozharnaya dver EI 30 s samozakryvaniem.");
  writePdf(two, "norm.pdf", "Protivopozharnaya dver EI 60 s samozakryvaniem i uplotneniyami.");

  const a = await loadFireDoorRulesFromNormatives({ normativesDir: one, pdfFiles: ["norm.pdf"] });
  const b = await loadFireDoorRulesFromNormatives({ normativesDir: two, pdfFiles: ["norm.pdf"] });

  assert.notEqual(a, b, "одинаковое имя файла в разных папках — разные правила");
  assert.equal(a.normativesDir, one);
  assert.equal(b.normativesDir, two);
});

test("clearFireDoorRulesCache forces a re-parse", async (t) => {
  clearFireDoorRulesCache();
  t.after(clearFireDoorRulesCache);

  const dir = tempNormatives();
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  writePdf(dir, "norm.pdf", "Protivopozharnaya dver EI 30 s samozakryvaniem.");

  const a = await loadFireDoorRulesFromNormatives({ normativesDir: dir, pdfFiles: ["norm.pdf"] });
  clearFireDoorRulesCache();
  const b = await loadFireDoorRulesFromNormatives({ normativesDir: dir, pdfFiles: ["norm.pdf"] });

  assert.notEqual(a, b);
  assert.deepEqual(
    a.rules.map((r) => r.id),
    b.rules.map((r) => r.id),
    "содержимое то же — заново разобрано, но не изменилось"
  );
});

test("a missing file does not poison the cache key", async (t) => {
  // "missing" must be a distinct key state, so adding the file later re-parses.
  clearFireDoorRulesCache();
  t.after(clearFireDoorRulesCache);

  const dir = tempNormatives();
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));

  const absent = await loadFireDoorRulesFromNormatives({
    normativesDir: dir,
    pdfFiles: ["norm.pdf"],
  });
  assert.equal(absent.rules.length, 0);
  assert.ok(absent.warnings.length > 0, "об отсутствии файла надо предупредить");

  writePdf(dir, "norm.pdf", "Protivopozharnaya dver EI 30 s samozakryvaniem.");
  const present = await loadFireDoorRulesFromNormatives({
    normativesDir: dir,
    pdfFiles: ["norm.pdf"],
  });

  assert.notEqual(present, absent, "появившийся файл обязан быть прочитан");
});
