import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { PlannedOpening } from "../cad/cadOpeningTracing.js";
import { verifyPlacedOpenings } from "./trace_openings_from_cad.js";

function plan(x: number, y: number): PlannedOpening {
  return {
    kind: "door",
    centerMm: { x, y, z: 0 },
    widthMm: 900,
    sourceCadIds: [],
    bboxMm: { minX: x - 450, minY: y - 50, maxX: x + 450, maxY: y + 50 },
    segmentCount: 1,
    hostWallId: 1,
    locationMm: { x, y, z: 0 },
    hostDistanceMm: 0,
    paramT: 0.5,
  };
}

/** Stub Revit that reports where each door actually ended up. */
function clientWith(actual: Array<{ id: number; x: number; y: number }>) {
  return {
    async sendCommand(cmd: string) {
      if (cmd !== "get_current_view_elements") throw new Error(`unexpected ${cmd}`);
      return {
        Elements: actual.map((a) => ({
          Id: a.id,
          Properties: { LocationXMm: String(a.x), LocationYMm: String(a.y) },
        })),
      };
    },
  };
}

describe("verifyPlacedOpenings (REV-149)", () => {
  it("passes when every door landed on its CAD point", async () => {
    const planned = [plan(1000, 0), plan(3000, 0)];
    const result = await verifyPlacedOpenings(
      clientWith([
        { id: 101, x: 1000, y: 0 },
        { id: 102, x: 3000, y: 0 },
      ]),
      planned,
      [101, 102],
      100
    );

    assert.equal(result.ok, true);
    assert.equal(result.checked, 2);
    assert.equal(result.maxDeviationMm, 0);
  });

  it("reports a door Revit moved off the underlay", async () => {
    const planned = [plan(1000, 0), plan(3000, 0)];
    const result = await verifyPlacedOpenings(
      clientWith([
        { id: 101, x: 1000, y: 0 },
        // Revit nudged this one 600 mm along the wall — the failure mode REV-147 hid.
        { id: 102, x: 3600, y: 0 },
      ]),
      planned,
      [101, 102],
      100
    );

    assert.equal(result.ok, false);
    assert.equal(result.failedCount, 1);
    assert.equal(result.maxDeviationMm, 600);
    assert.equal(result.items[1].ok, false);
  });

  it("keeps plan↔element alignment when a middle item failed to create", async () => {
    // Three planned, the middle one errored: ids must not shift up by one, or the
    // third door would be checked against the second plan and look fine.
    const planned = [plan(1000, 0), plan(3000, 0), plan(5000, 0)];
    const result = await verifyPlacedOpenings(
      clientWith([
        { id: 101, x: 1000, y: 0 },
        { id: 103, x: 5000, y: 0 },
      ]),
      planned,
      [101, null, 103],
      100
    );

    assert.equal(result.checked, 2);
    assert.equal(result.ok, true);
    assert.equal(result.items[1].elementId, 103);
    assert.deepEqual(result.items[1].plannedCenterMm, { x: 5000, y: 0, z: 0 });
  });

  it("counts an element it could not read back as a failure, not a pass", async () => {
    const result = await verifyPlacedOpenings(
      clientWith([]),
      [plan(1000, 0)],
      [101],
      100
    );

    assert.equal(result.ok, false);
    assert.equal(result.unreadable, 1);
    assert.equal(result.items[0].deviationMm, null);
  });

  it("is a no-op when nothing was created", async () => {
    const result = await verifyPlacedOpenings(
      clientWith([]),
      [plan(1000, 0)],
      [null],
      100
    );

    assert.equal(result.ok, true);
    assert.equal(result.checked, 0);
  });
});
