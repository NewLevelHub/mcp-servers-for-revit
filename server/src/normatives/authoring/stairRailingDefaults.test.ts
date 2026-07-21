import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { Database } from "better-sqlite3";
import {
  resolveRailingAuthoringDefaults,
  resolveStairAuthoringDefaults,
} from "./stairRailingDefaults.js";
import type { NormAuditSource } from "../normAudit/types.js";

const fakeDb = {} as Database;

const source: NormAuditSource = {
  document: "СП РК 3.02-101-2012",
  clause: "4.x",
  quote: "ширина марша не менее 1050 мм",
};

const baseStair = {
  typeId: 100,
  baseLevelId: 1,
  topLevelId: 2,
  startPoint: { x: 0, y: 0 },
  endPoint: { x: 3000, y: 0 },
};

describe("resolveStairAuthoringDefaults", () => {
  it("uses explicit widthMm and does not call library", () => {
    let called = false;
    const result = resolveStairAuthoringDefaults(fakeDb, { ...baseStair, widthMm: 1200 }, {
      resolveWidth: () => {
        called = true;
        return null;
      },
      resolveRiserTread: () => null,
    });
    assert.equal(result.ok, true);
    if (!result.ok) return;
    assert.equal(result.value.widthMm, 1200);
    assert.equal(called, false);
  });

  it("resolves width from library when omitted", () => {
    const result = resolveStairAuthoringDefaults(fakeDb, baseStair, {
      resolveWidth: () => ({
        minWidthMm: 1050,
        source,
        rule: {} as never,
      }),
      resolveRiserTread: () => null,
    });
    assert.equal(result.ok, true);
    if (!result.ok) return;
    assert.equal(result.value.widthMm, 1050);
    assert.equal(result.value.normSource?.document, source.document);
  });

  it("fails when width omitted and library empty", () => {
    const result = resolveStairAuthoringDefaults(fakeDb, baseStair, {
      resolveWidth: () => null,
      resolveRiserTread: () => null,
    });
    assert.equal(result.ok, false);
    if (result.ok) return;
    assert.match(result.error, /widthMm omitted/i);
    assert.match(result.error, /seed/i);
  });

  it("fails when typeId missing", () => {
    const result = resolveStairAuthoringDefaults(
      fakeDb,
      { ...baseStair, typeId: 0, widthMm: 1200 },
      {
        resolveWidth: () => null,
        resolveRiserTread: () => null,
      }
    );
    assert.equal(result.ok, false);
    if (result.ok) return;
    assert.match(result.error, /typeId/i);
  });
});

describe("resolveRailingAuthoringDefaults", () => {
  it("resolves height from library for host mode", () => {
    const result = resolveRailingAuthoringDefaults(
      fakeDb,
      { typeId: 50, hostElementId: 999 },
      {
        resolveHeight: () => ({
          minHeightMm: 1200,
          source,
          rule: {} as never,
        }),
      }
    );
    assert.equal(result.ok, true);
    if (!result.ok) return;
    assert.equal(result.value.heightMm, 1200);
    assert.ok(result.value.warnings.length > 0);
  });

  it("fails when height omitted and library empty", () => {
    const result = resolveRailingAuthoringDefaults(
      fakeDb,
      { typeId: 50, hostElementId: 999 },
      { resolveHeight: () => null }
    );
    assert.equal(result.ok, false);
    if (result.ok) return;
    assert.match(result.error, /heightMm omitted/i);
  });

  it("rejects host and path together", () => {
    const result = resolveRailingAuthoringDefaults(
      fakeDb,
      {
        typeId: 50,
        hostElementId: 999,
        pathPoints: [
          { x: 0, y: 0 },
          { x: 1000, y: 0 },
        ],
        levelId: 1,
        heightMm: 1200,
      },
      { resolveHeight: () => null }
    );
    assert.equal(result.ok, false);
    if (result.ok) return;
    assert.match(result.error, /not both/i);
  });

  it("requires levelId for path mode", () => {
    const result = resolveRailingAuthoringDefaults(
      fakeDb,
      {
        typeId: 50,
        pathPoints: [
          { x: 0, y: 0 },
          { x: 1000, y: 0 },
        ],
        heightMm: 1200,
      },
      { resolveHeight: () => null }
    );
    assert.equal(result.ok, false);
    if (result.ok) return;
    assert.match(result.error, /levelId/i);
  });
});
