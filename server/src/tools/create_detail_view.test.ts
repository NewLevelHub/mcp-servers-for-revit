import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { z } from "zod";

/**
 * Mirrors the create_detail_view zod schema, focused on the section mode added alongside
 * callout and drafting.
 */
const pointSchema = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number().optional().default(0),
});

const schema = z.object({
  mode: z.enum(["callout", "drafting", "section"]).optional().default("callout"),
  name: z.string().optional(),
  scale: z.number().int().optional().default(10),
  detailLevel: z.enum(["Coarse", "Medium", "Fine"]).optional().default("Fine"),
  activateView: z.boolean().optional().default(true),
  parentViewId: z.number().int().optional(),
  elementId: z.number().int().optional(),
  padding: z.number().optional().default(300),
  areaMin: pointSchema.optional(),
  areaMax: pointSchema.optional(),
  sectionStart: pointSchema.optional(),
  sectionEnd: pointSchema.optional(),
  sectionBottomMm: z.number().optional(),
  sectionTopMm: z.number().optional(),
  sectionDepthMm: z.number().optional(),
  sectionAlongX: z.boolean().optional(),
  flip: z.boolean().optional(),
});

describe("create_detail_view schema", () => {
  it("accepts a section from an explicit cutting line", () => {
    const parsed = schema.safeParse({
      mode: "section",
      sectionStart: { x: 0, y: 0 },
      sectionEnd: { x: 6000, y: 0 },
      sectionBottomMm: 0,
      sectionTopMm: 3000,
      sectionDepthMm: 2000,
    });

    assert.equal(parsed.success, true);
  });

  it("accepts a section across an element with flip", () => {
    const parsed = schema.safeParse({
      mode: "section",
      elementId: 123456,
      sectionAlongX: false,
      flip: true,
    });

    assert.equal(parsed.success, true);
  });

  it("still accepts the original callout and drafting modes", () => {
    assert.equal(schema.safeParse({ mode: "callout", elementId: 1 }).success, true);
    assert.equal(schema.safeParse({ mode: "drafting" }).success, true);
  });

  it("rejects an unknown mode", () => {
    assert.equal(schema.safeParse({ mode: "elevation" }).success, false);
  });

  it("defaults to a callout at 1:10 with fine detail", () => {
    const parsed = schema.parse({});

    assert.equal(parsed.mode, "callout");
    assert.equal(parsed.scale, 10);
    assert.equal(parsed.detailLevel, "Fine");
  });
});
