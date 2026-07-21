import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { z } from "zod";

/**
 * Mirrors create_floor_opening zod refine rules (REV-85) without loading MCP server.
 */
const pointMm = z.object({ x: z.number(), y: z.number(), z: z.number().optional() });
const rectMm = z.object({
  origin: pointMm,
  widthMm: z.number().positive(),
  depthMm: z.number().positive(),
  rotationDeg: z.number().optional(),
});

const itemSchema = z
  .object({
    mode: z.enum(["floor", "shaft"]).optional().default("floor"),
    hostFloorId: z.number().int().positive().optional(),
    levelId: z.number().int().positive().optional(),
    baseLevelId: z.number().int().positive().optional(),
    topLevelId: z.number().int().positive().optional(),
    boundaryPoints: z.array(pointMm).min(3).optional(),
    rect: rectMm.optional(),
  })
  .superRefine((item, ctx) => {
    const mode = item.mode ?? "floor";
    const hasBoundary =
      Array.isArray(item.boundaryPoints) && item.boundaryPoints.length >= 3;
    const hasRect = item.rect != null;
    if (hasBoundary === hasRect) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: "Provide exactly one of boundaryPoints or rect.",
      });
    }
    if (mode === "floor" && item.hostFloorId == null && item.levelId == null) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: "mode=floor requires hostFloorId or levelId.",
      });
    }
    if (mode === "shaft" && (item.baseLevelId == null || item.topLevelId == null)) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: "mode=shaft requires baseLevelId and topLevelId.",
      });
    }
  });

describe("create_floor_opening schema (REV-85)", () => {
  it("accepts floor + rect", () => {
    const parsed = itemSchema.safeParse({
      mode: "floor",
      hostFloorId: 42,
      rect: { origin: { x: 0, y: 0 }, widthMm: 2500, depthMm: 2500 },
    });
    assert.equal(parsed.success, true);
  });

  it("rejects floor without host or level", () => {
    const parsed = itemSchema.safeParse({
      mode: "floor",
      rect: { origin: { x: 0, y: 0 }, widthMm: 1000, depthMm: 1000 },
    });
    assert.equal(parsed.success, false);
  });

  it("rejects both boundary and rect", () => {
    const parsed = itemSchema.safeParse({
      mode: "floor",
      levelId: 1,
      boundaryPoints: [
        { x: 0, y: 0 },
        { x: 1, y: 0 },
        { x: 1, y: 1 },
      ],
      rect: { origin: { x: 0, y: 0 }, widthMm: 1000, depthMm: 1000 },
    });
    assert.equal(parsed.success, false);
  });

  it("accepts shaft with levels + boundary", () => {
    const parsed = itemSchema.safeParse({
      mode: "shaft",
      baseLevelId: 10,
      topLevelId: 20,
      boundaryPoints: [
        { x: 0, y: 0 },
        { x: 2500, y: 0 },
        { x: 2500, y: 2500 },
        { x: 0, y: 2500 },
      ],
    });
    assert.equal(parsed.success, true);
  });

  it("rejects shaft without topLevelId", () => {
    const parsed = itemSchema.safeParse({
      mode: "shaft",
      baseLevelId: 10,
      rect: { origin: { x: 0, y: 0 }, widthMm: 2000, depthMm: 2000 },
    });
    assert.equal(parsed.success, false);
  });
});
