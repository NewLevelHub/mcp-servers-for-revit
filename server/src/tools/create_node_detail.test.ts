import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { z } from "zod";

/**
 * Mirrors the create_node_detail zod schema without loading the MCP server.
 */
const extraLayerSchema = z.object({
  name: z.string(),
  thicknessMm: z.number().min(0),
  target: z.enum(["floor", "wall"]).optional(),
  function: z.string().optional(),
  fillPatternName: z.string().optional(),
  insertAt: z.number().int().optional(),
});

const schema = z.object({
  mode: z.enum(["junction", "single"]).optional(),
  wallTypeId: z.number().int().optional(),
  floorTypeId: z.number().int().optional(),
  orientation: z.enum(["horizontal", "vertical"]).optional(),
  name: z.string().optional(),
  scale: z.number().int().optional(),
  lengthMm: z.number().optional(),
  wallRunMm: z.number().optional(),
  gapMm: z.number().optional(),
  annotate: z.boolean().optional(),
  drawHatches: z.boolean().optional(),
  createMissingTypes: z.boolean().optional(),
  dimensionTypeName: z.string().optional(),
  textTypeName: z.string().optional(),
  lineStyleName: z.string().optional(),
  extraLayers: z.array(extraLayerSchema).optional(),
  sheetNumber: z.string().optional(),
  activateView: z.boolean().optional(),
});

describe("create_node_detail schema", () => {
  it("accepts a junction of a wall and a floor", () => {
    const parsed = schema.safeParse({
      mode: "junction",
      wallTypeId: 1001,
      floorTypeId: 2002,
      scale: 10,
      gapMm: 20,
    });

    assert.equal(parsed.success, true);
  });

  it("accepts build-up layers the type does not carry", () => {
    const parsed = schema.safeParse({
      mode: "junction",
      wallTypeId: 1001,
      floorTypeId: 2002,
      extraLayers: [
        { name: "Звукоизоляция", thicknessMm: 20, target: "floor", insertAt: 1 },
        { name: "Пароизоляция", thicknessMm: 0, fillPatternName: "Solid fill" },
      ],
    });

    assert.equal(parsed.success, true);
  });

  it("rejects a negative layer thickness", () => {
    const parsed = schema.safeParse({
      wallTypeId: 1001,
      extraLayers: [{ name: "Подложка", thicknessMm: -5 }],
    });

    assert.equal(parsed.success, false);
  });

  it("rejects an unknown mode", () => {
    assert.equal(schema.safeParse({ mode: "isometric" }).success, false);
  });

  it("rejects an unknown extra layer target", () => {
    const parsed = schema.safeParse({
      extraLayers: [{ name: "Подложка", thicknessMm: 2, target: "roof" }],
    });

    assert.equal(parsed.success, false);
  });

  it("leaves every id optional so single mode needs only one assembly", () => {
    assert.equal(schema.safeParse({ mode: "single", floorTypeId: 2002 }).success, true);
  });
});
