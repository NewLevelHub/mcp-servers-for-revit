import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCreateLineBasedElementTool(server: McpServer) {
  server.tool(
    "create_line_based_element",
    "Create one or more line-based elements in Revit such as walls, beams, or pipes. " +
      "Requires typeId from get_available_family_types. Missing typeId fails (no FirstOrDefault fallback). " +
      "Batch: one Transaction for the whole data[]. Units: mm.",
    {
      data: z
        .array(
          z.object({
            category: z
              .string()
              .describe("Revit built-in category (e.g., OST_Walls, OST_StructuralFraming, OST_DuctCurves)"),
            typeId: z
              .number()
              .describe("Required. Family/WallType ElementId from get_available_family_types. Missing or invalid typeId fails (no FirstOrDefault fallback)."),
            locationLine: z
              .object({
                p0: z.object({
                  x: z.number().describe("X coordinate of start point"),
                  y: z.number().describe("Y coordinate of start point"),
                  z: z.number().describe("Z coordinate of start point"),
                }),
                p1: z.object({
                  x: z.number().describe("X coordinate of end point"),
                  y: z.number().describe("Y coordinate of end point"),
                  z: z.number().describe("Z coordinate of end point"),
                }),
                pointOnCurve: z
                  .object({
                    x: z.number(),
                    y: z.number(),
                    z: z.number(),
                  })
                  .optional()
                  .describe(
                    "Optional third point (mm) the location curve passes through. Set it to create a curved wall — Revit builds an arc through p0, pointOnCurve and p1. Walls only; ignored for ducts and family instances."
                  ),
              })
              .describe(
                "The line defining the element's location. Add pointOnCurve for an arc (curved wall)."
              ),
            thickness: z
              .number()
              .describe(
                "Informational only — wall thickness comes from typeId (WallType compound structure), not this field."
              ),
            height: z
              .number()
              .describe("Height of the element (e.g., wall height)"),
            baseLevel: z.number().describe("Base level height"),
            baseOffset: z.number().describe("Offset from the base level"),
          })
        )
        .describe("Array of line-based elements to create"),
    },
    async (args, extra) => {
      const params = args;

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(
            "create_line_based_element",
            params
          );
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Create line-based element failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
