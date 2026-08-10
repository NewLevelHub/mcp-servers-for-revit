import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { CadSegment } from "./cadWallTracing.js";
import {
  extractArcSwings,
  openingsFromArcSwings,
  resolveArcSwingOnWall,
  matchArcOpeningToHost,
  matchOpeningToHost,
  traceOpeningsFromCad,
  type HostWall,
} from "./cadOpeningTracing.js";

/**
 * Build the chord(s) of a door swing arc the way get_cad_link_geometry returns them:
 * endpoints plus the arc centre/radius/angles that survive the trip through Revit.
 */
function arcChord(
  centerX: number,
  centerY: number,
  radius: number,
  startDeg: number,
  endDeg: number,
  layer = "A-DOOR",
  arcId = "arc-1"
): CadSegment {
  const s = (startDeg * Math.PI) / 180;
  const e = (endDeg * Math.PI) / 180;
  const start = { x: centerX + radius * Math.cos(s), y: centerY + radius * Math.sin(s), z: 0 };
  const end = { x: centerX + radius * Math.cos(e), y: centerY + radius * Math.sin(e), z: 0 };
  return {
    startMm: start,
    endMm: end,
    lengthMm: Math.hypot(end.x - start.x, end.y - start.y),
    layer,
    cadId: arcId,
    curveType: "arc",
    arcId,
    arcCenterMm: { x: centerX, y: centerY, z: 0 },
    arcRadiusMm: radius,
    arcStartAngleDeg: startDeg,
    arcEndAngleDeg: endDeg,
  };
}

describe("cadOpeningTracing arcs (REV-149)", () => {
  it("rebuilds a swing arc from chord metadata", () => {
    // Hinge at (1000, 0), 900 mm leaf, closed along +X, swings to +Y.
    const swings = extractArcSwings([arcChord(1000, 0, 900, 0, 90)]);
    assert.equal(swings.length, 1);
    assert.equal(swings[0].radiusMm, 900);
    assert.equal(Math.round(swings[0].hingeMm.x), 1000);
    assert.equal(Math.round(swings[0].endAMm.x), 1900);
    assert.equal(Math.round(swings[0].endBMm.y), 900);
  });

  it("ignores arcs outside the door width band", () => {
    // A 300 mm furniture arc and a 4 m layout arc are not doors.
    const swings = extractArcSwings([
      arcChord(0, 0, 300, 0, 90, "A-DOOR", "small"),
      arcChord(5000, 0, 4000, 0, 90, "A-DOOR", "huge"),
    ]);
    assert.equal(swings.length, 0);
  });

  it("groups every chord of one arc into a single swing", () => {
    // Tessellated arcs repeat the same arcId — the door must not be counted twice.
    const chords = [
      arcChord(1000, 0, 900, 0, 45, "A-DOOR", "arc-7"),
      arcChord(1000, 0, 900, 45, 90, "A-DOOR", "arc-7"),
    ];
    assert.equal(extractArcSwings(chords).length, 1);
  });

  it("resolves hinge, centre, width and swing side against the wall", () => {
    const [swing] = extractArcSwings([arcChord(1000, 0, 900, 0, 90)]);
    const resolved = resolveArcSwingOnWall(swing, { x: 1, y: 0 });
    assert.ok(resolved);
    // Closed leaf runs 1000 → 1900 along the wall, so the opening centres at 1450.
    assert.equal(Math.round(resolved!.centerMm.x), 1450);
    assert.equal(Math.round(resolved!.centerMm.y), 0);
    assert.equal(resolved!.widthMm, 900);
    // Door opens toward +Y, hinge sits back toward -X from the centre.
    assert.equal(Math.round(resolved!.swingNormal.y), 1);
    assert.equal(Math.round(resolved!.hingeDir.x), -1);
  });

  it("mirrors the hinge side when the swing is drawn the other way", () => {
    // Same wall, hinge on the right: closed leaf runs 1900 → 1000.
    const [swing] = extractArcSwings([arcChord(1900, 0, 900, 180, 90)]);
    const resolved = resolveArcSwingOnWall(swing, { x: 1, y: 0 });
    assert.ok(resolved);
    assert.equal(Math.round(resolved!.centerMm.x), 1450);
    assert.equal(Math.round(resolved!.hingeDir.x), 1);
  });

  it("returns null when neither arc endpoint lies along the wall", () => {
    // Arc perpendicular to the wall — not a door on this wall.
    const [swing] = extractArcSwings([arcChord(1000, 0, 900, 45, 135)]);
    assert.equal(resolveArcSwingOnWall(swing, { x: 1, y: 0 }), null);
  });

  it("hosts an arc door on the wall it belongs to and keeps the CAD centre", () => {
    const [opening] = openingsFromArcSwings(
      extractArcSwings([arcChord(1000, 0, 900, 0, 90)]),
      "door"
    );
    const walls: HostWall[] = [
      { id: 11, startMm: { x: 0, y: 0, z: 0 }, endMm: { x: 5000, y: 0, z: 0 } },
      // A perpendicular partition at the hinge — this is what used to steal the door.
      { id: 12, startMm: { x: 1000, y: 0, z: 0 }, endMm: { x: 1000, y: 4000, z: 0 } },
    ];

    const planned = matchArcOpeningToHost(opening, walls, {});
    assert.ok(planned);
    assert.equal(planned!.hostWallId, 11);
    assert.equal(Math.round(planned!.locationMm.x), 1450);
    assert.equal(planned!.widthMm, 900);
    assert.ok(planned!.hingeDir);
    assert.equal(Math.round(planned!.hingeDir!.x), -1);
  });

  it("does not host an arc door on a wall that cannot fit the leaf", () => {
    const [opening] = openingsFromArcSwings(
      extractArcSwings([arcChord(1000, 0, 900, 0, 90)]),
      "door"
    );
    // Wall ends at 1500: the 900 mm leaf running 1000 → 1900 does not fit.
    const walls: HostWall[] = [
      { id: 21, startMm: { x: 0, y: 0, z: 0 }, endMm: { x: 1500, y: 0, z: 0 } },
    ];
    const planned = matchArcOpeningToHost(opening, walls, { allowBridge: false });
    assert.equal(planned, null);
  });

  it("routes arc openings through matchOpeningToHost", () => {
    const [opening] = openingsFromArcSwings(
      extractArcSwings([arcChord(1000, 0, 900, 0, 90)]),
      "door"
    );
    const walls: HostWall[] = [
      { id: 31, startMm: { x: 0, y: 0, z: 0 }, endMm: { x: 5000, y: 0, z: 0 } },
    ];
    const planned = matchOpeningToHost(opening, walls, {});
    assert.equal(planned?.hostWallId, 31);
  });

  it("prefers arcs over the leaf heuristics and reports the detection mode", () => {
    const segments: CadSegment[] = [
      arcChord(1000, 0, 900, 0, 90),
      // Straight leaf line of the same door — the old path would count it separately.
      {
        startMm: { x: 1000, y: 0, z: 0 },
        endMm: { x: 1000, y: 900, z: 0 },
        lengthMm: 900,
        layer: "A-DOOR",
        cadId: "leaf-1",
      },
    ];

    const traced = traceOpeningsFromCad(segments, { kind: "door" });
    assert.equal(traced.stats.detection, "arc");
    assert.equal(traced.stats.arcSwings, 1);
    assert.equal(traced.openings.length, 1);
    assert.equal(traced.openings[0].detection, "arc");
  });

  it("falls back to the old heuristics when the DWG carries no arc metadata", () => {
    const segments: CadSegment[] = [
      {
        startMm: { x: 0, y: 0, z: 0 },
        endMm: { x: 900, y: 0, z: 0 },
        lengthMm: 900,
        layer: "A-DOOR-MCUT",
        cadId: "leaf-a",
      },
    ];
    const traced = traceOpeningsFromCad(segments, { kind: "door" });
    assert.equal(traced.stats.detection, "leaf");
    assert.equal(traced.stats.arcSwings, 0);
    assert.equal(traced.openings.length, 1);
  });

  it("keeps windows on the non-arc path", () => {
    // Windows have no swing; an arc on a glazing layer must not become a window.
    const traced = traceOpeningsFromCad([arcChord(1000, 0, 900, 0, 90, "A-GLAZ")], {
      kind: "window",
    });
    assert.equal(traced.stats.arcSwings, 0);
  });
});
