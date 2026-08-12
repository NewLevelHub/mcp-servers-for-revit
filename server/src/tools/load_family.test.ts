import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { z } from "zod";

/**
 * Mirrors the load_family zod schema without loading the MCP server.
 */
const schema = z.object({
  paths: z.array(z.string()).optional(),
  directory: z.string().optional(),
  names: z.array(z.string()).optional(),
  overwriteParameterValues: z.boolean().optional(),
  activateSymbols: z.boolean().optional(),
});

describe("load_family schema", () => {
  it("accepts explicit paths", () => {
    const parsed = schema.safeParse({
      paths: ["C:\\Families\\Узел_кромка.rfa", "C:\\Families\\Плинтус.rfa"],
    });

    assert.equal(parsed.success, true);
  });

  it("accepts a directory with names", () => {
    const parsed = schema.safeParse({
      directory: "C:\\Families",
      names: ["Плинтус", "Кромка.rfa"],
    });

    assert.equal(parsed.success, true);
  });

  it("accepts a directory alone", () => {
    assert.equal(schema.safeParse({ directory: "C:\\Families" }).success, true);
  });

  it("rejects a non-string path", () => {
    assert.equal(schema.safeParse({ paths: [42] }).success, false);
  });

  it("leaves the .rfa check to Revit-side validation, not the schema", () => {
    // Path validation needs the filesystem of the machine running Revit, so the schema
    // stays permissive and the handler reports a named warning instead.
    assert.equal(schema.safeParse({ paths: ["C:\\Families\\notepad.exe"] }).success, true);
  });
});
