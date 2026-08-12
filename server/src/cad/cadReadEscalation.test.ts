import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  readWithLimitEscalation,
  truncationWarning,
} from "./cadReadEscalation.js";

/** Fake Revit: holds `total` segments and truncates whatever exceeds the limit. */
function fakeCad(total: number) {
  const limits: number[] = [];
  const fetch = async (limit: number) => {
    limits.push(limit);
    const count = Math.min(limit, total);
    return { truncated: count < total, count, items: new Array(count).fill(0) };
  };
  return { fetch, limits };
}

describe("readWithLimitEscalation", () => {
  it("reads once when the drawing fits the requested limit", async () => {
    const cad = fakeCad(1200);
    const r = await readWithLimitEscalation(cad.fetch, 5000);

    assert.deepEqual(cad.limits, [5000]);
    assert.equal(r.rereads, 0);
    assert.equal(r.stillTruncated, false);
    assert.equal(r.response.count, 1200);
  });

  it("escalates until the whole DWG is read", async () => {
    // «двг2.dwg»: 19 011 segments against the default limit of 5 000.
    const cad = fakeCad(19011);
    const r = await readWithLimitEscalation(cad.fetch, 5000);

    assert.deepEqual(cad.limits, [5000, 20000]);
    assert.equal(r.rereads, 1);
    assert.equal(r.stillTruncated, false);
    assert.equal(r.response.count, 19011);
    assert.equal(truncationWarning(r), null);
  });

  it("stops at maxLimit and reports the read as partial", async () => {
    const cad = fakeCad(500000);
    const r = await readWithLimitEscalation(cad.fetch, 5000, 20000);

    assert.deepEqual(cad.limits, [5000, 20000]);
    assert.equal(r.limitUsed, 20000);
    assert.equal(r.stillTruncated, true);
    assert.match(String(truncationWarning(r)), /НЕ учтены/);
  });

  it("never asks for more than maxLimit", async () => {
    const cad = fakeCad(500000);
    const r = await readWithLimitEscalation(cad.fetch, 50000, 60000);

    assert.deepEqual(cad.limits, [50000, 60000]);
    assert.equal(r.limitUsed, 60000);
  });

  it("clamps a caller limit that already exceeds maxLimit", async () => {
    const cad = fakeCad(10);
    const r = await readWithLimitEscalation(cad.fetch, 999999, 60000);

    assert.deepEqual(cad.limits, [60000]);
    assert.equal(r.stillTruncated, false);
  });
});
