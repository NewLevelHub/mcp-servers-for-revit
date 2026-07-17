import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerDimensionGridsTool(server: McpServer) {
  server.tool(
    "dimension_grids",
    "Create exterior axial dimension chains on the active floor plan. Offsets are measured from the FULL building envelope (all walls including loggias/balconies/entrances), NOT from grid axis coordinates. Creates inter-axis + overall tiers: numbers at bottom, letters at left. Preferred after create_grid / when grids already exist. Type ADSK_Основной_2.5 мм by default.",
    {
      gridIds: z
        .array(z.number().int().positive())
        .optional()
        .default([])
        .describe("Grid element IDs. Empty = all grids in the document."),
      firstOffsetMm: z
        .number()
        .positive()
        .optional()
        .default(1200)
        .describe(
          "Offset of the first (inter-axis) chain beyond the building envelope (mm). Default 1200 — clears walls/loggias."
        ),
      tierGapMm: z
        .number()
        .positive()
        .optional()
        .default(800)
        .describe("Gap between inter-axis and overall dimension tiers (mm). Default 800."),
      includeOverall: z
        .boolean()
        .optional()
        .default(true)
        .describe("Also create overall (extreme-grid) chains on the outer tier."),
      numericSide: z
        .enum(["bottom", "top"])
        .optional()
        .default("bottom")
        .describe("Side for numeric/vertical-grid chains. Working drawings: bottom."),
      letterSide: z
        .enum(["left", "right"])
        .optional()
        .default("left")
        .describe("Side for letter/horizontal-grid chains. Working drawings: left."),
      dimensionType: z
        .string()
        .optional()
        .default("ADSK_Основной_2.5 мм")
        .describe("DimensionType name from the project."),
      dimensionStyleId: z
        .number()
        .optional()
        .default(-1)
        .describe("Dimension style element ID. -1 = resolve by name."),
      viewId: z
        .number()
        .optional()
        .default(-1)
        .describe("View ID. -1 = active floor plan."),
      envelopePaddingMm: z
        .number()
        .optional()
        .default(250)
        .describe("Extra padding on wall bbox for face thickness (mm)."),
      extendGridExtents: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Extend grid 2D extents past outer tier so bubbles sit outside dimensions (bottomLeft)."
        ),
      bubbleClearanceMm: z
        .number()
        .positive()
        .optional()
        .default(1200)
        .describe("Extra overshoot beyond outer tier for bubbles (mm)."),
    },
    async (args, extra) => {
      const params = {
        gridIds: args.gridIds ?? [],
        firstOffsetMm: args.firstOffsetMm,
        tierGapMm: args.tierGapMm,
        includeOverall: args.includeOverall,
        numericSide: args.numericSide,
        letterSide: args.letterSide,
        dimensionType: args.dimensionType,
        dimensionStyleId: args.dimensionStyleId,
        viewId: args.viewId,
        envelopePaddingMm: args.envelopePaddingMm,
        extendGridExtents: args.extendGridExtents,
        bubbleClearanceMm: args.bubbleClearanceMm,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("dimension_grids", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Axial grid dimensioning failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
