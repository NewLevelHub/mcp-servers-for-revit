import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { WARNING_CATALOG, NEVER_AUTO_FIX_GUIDS, explainWarning } from "./warningCatalog.js";

describe("WARNING_CATALOG", () => {
  it("every entry's key matches its own guid field", () => {
    for (const [key, entry] of Object.entries(WARNING_CATALOG)) {
      assert.equal(entry.guid, key);
    }
  });

  it("every entry has non-empty title, risk and fix text", () => {
    for (const entry of Object.values(WARNING_CATALOG)) {
      assert.ok(entry.title.trim().length > 0, `${entry.guid} missing title`);
      assert.ok(entry.risk.trim().length > 0, `${entry.guid} missing risk`);
      assert.ok(entry.fix.trim().length > 0, `${entry.guid} missing fix`);
    }
  });

  it("dangerRank is always 1-4", () => {
    for (const entry of Object.values(WARNING_CATALOG)) {
      assert.ok(entry.dangerRank >= 1 && entry.dangerRank <= 4, entry.guid);
    }
  });

  it("has at least one autoFixable entry (the redundant room-separator case)", () => {
    const autoFixable = Object.values(WARNING_CATALOG).filter((e) => e.autoFixable);
    assert.equal(autoFixable.length, 1);
    assert.equal(autoFixable[0].guid, "f7b3a015-c3eb-4a3f-b345-c474ec07d43f");
  });

  it("room-drift/duplicate warnings rank most urgent (dangerRank 1)", () => {
    // REV-180: "дубли элементов и разъехавшиеся помещения вперёд".
    const roomDrift = [
      "83d4a67c-818c-4291-adaf-f2d33064fea8", // two rooms in one boundary
      "a22de05c-4c92-4bdc-9ce3-a965d2cf316c", // room volumes overlap
      "4f0bba25-e17f-480a-a763-d97d184be18a", // room tag outside its room
    ];
    for (const guid of roomDrift) {
      assert.equal(WARNING_CATALOG[guid].dangerRank, 1, guid);
    }
  });

  it("the everyday wall-overlap-at-joins warning ranks least urgent, despite huge volume", () => {
    // REV-180: "«слегка вне оси» в конец" — this is that role in the real catalog.
    assert.equal(WARNING_CATALOG["988a6cb2-7050-4a5c-a946-60d652df66c3"].dangerRank, 4);
  });
});

describe("NEVER_AUTO_FIX_GUIDS", () => {
  it("contains every non-autoFixable entry and none of the autoFixable ones", () => {
    for (const entry of Object.values(WARNING_CATALOG)) {
      assert.equal(NEVER_AUTO_FIX_GUIDS.has(entry.guid), !entry.autoFixable, entry.guid);
    }
  });

  it("explicitly covers the dangerous cases a careless auto-fix could ruin", () => {
    // Deleting the "wrong" one destroys real data (name/number) with no way back.
    assert.ok(NEVER_AUTO_FIX_GUIDS.has("83d4a67c-818c-4291-adaf-f2d33064fea8"));
    // Ambiguous: could be a real duplicate, could be deliberate nested construction.
    assert.ok(NEVER_AUTO_FIX_GUIDS.has("505d84a1-67e4-4987-8287-21ad1792ffe9"));
  });
});

describe("explainWarning", () => {
  it("returns the catalog entry for a known guid", () => {
    const result = explainWarning("988a6cb2-7050-4a5c-a946-60d652df66c3", "raw text");
    assert.equal(result.title, "Стены перекрываются");
    assert.equal(result.uncatalogued, false);
  });

  it("falls back gracefully for an unrecognized guid, using the raw Revit text", () => {
    const result = explainWarning("00000000-0000-0000-0000-000000000000", "какой-то текст Revit");
    assert.equal(result.uncatalogued, true);
    assert.equal(result.autoFixable, false);
    assert.match(result.fix, /какой-то текст Revit/);
  });

  it("never throws on an empty guid", () => {
    assert.doesNotThrow(() => explainWarning("", "текст"));
  });
});
