import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { z } from "zod";

/**
 * Mirrors the create_detail_regions zod schema without loading the MCP server.
 */
const pointSchema = z.object({ x: z.number(), y: z.number() });

const regionSchema = z.object({
  points: z.array(pointSchema).min(3),
  holes: z.array(z.array(pointSchema).min(3)).optional(),
  filledRegionTypeName: z.string().optional(),
  fillPatternName: z.string().optional(),
  label: z.string().optional(),
});

const schema = z.object({
  viewId: z.number().int().optional(),
  viewUniqueId: z.string().optional(),
  viewName: z.string().optional(),
  regions: z.array(regionSchema).min(1),
  filledRegionTypeName: z.string().optional(),
  createMissingTypes: z.boolean().optional(),
  clearPrevious: z.boolean().optional(),
  clearOnly: z.boolean().optional(),
  commentTag: z.string().optional(),
});

const square = [
  { x: 0, y: 0 },
  { x: 100, y: 0 },
  { x: 100, y: 100 },
  { x: 0, y: 100 },
];

describe("create_detail_regions schema", () => {
  it("accepts a contour with a hole and a hatch pattern", () => {
    const parsed = schema.safeParse({
      viewId: 12345,
      regions: [
        {
          points: square,
          holes: [
            [
              { x: 20, y: 20 },
              { x: 40, y: 20 },
              { x: 40, y: 40 },
            ],
          ],
          fillPatternName: "Бетон",
          label: "Плита",
        },
      ],
    });

    assert.equal(parsed.success, true);
  });

  it("rejects a contour that cannot enclose an area", () => {
    const parsed = schema.safeParse({
      regions: [{ points: [{ x: 0, y: 0 }, { x: 100, y: 0 }] }],
    });

    assert.equal(parsed.success, false);
  });

  it("rejects a hole that cannot enclose an area", () => {
    const parsed = schema.safeParse({
      regions: [{ points: square, holes: [[{ x: 0, y: 0 }, { x: 10, y: 0 }]] }],
    });

    assert.equal(parsed.success, false);
  });

  it("requires at least one region", () => {
    assert.equal(schema.safeParse({ regions: [] }).success, false);
  });

  it("does not require a view — the active one is used", () => {
    assert.equal(schema.safeParse({ regions: [{ points: square }] }).success, true);
  });
});
