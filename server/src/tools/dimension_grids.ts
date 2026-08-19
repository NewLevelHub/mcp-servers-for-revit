import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerDimensionGridsTool(server: McpServer) {
  server.tool(
    "dimension_grids",
    "Create the EXTERIOR dimension ladder for the whole building on the active floor plan. Offsets are " +
      "measured from the FULL building envelope (all walls including loggias/balconies/entrances), NOT " +
      "from grid axis coordinates. Three tiers by default: openings/piers (innermost) → inter-axis → " +
      "overall. Numbers at bottom, letters at left. Preferred after create_grid / when grids already " +
      "exist. Type ADSK_Основной_2.5 мм by default. For chains inside a single room use " +
      "dimension_room_walls; for one dimension between two elements or points use create_dimensions.",
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
        .describe(
          "OMIT unless the user asked for a specific offset. Offsets are model mm but the drawing is read on paper, so leaving this out lets Revit derive the ladder from the view scale (ГОСТ Р 21.101: 14 mm on paper to the first chain, 8 mm between chains) — at 1:100 that is 1400/2200/3000 mm. Passing a number pins the inter-axis chain there and stacks the rest around it."
        ),
      tierGapMm: z
        .number()
        .positive()
        .optional()
        .describe("OMIT unless asked. Gap between chains (mm); omitted = 8 mm on paper × view scale."),
      includeOverall: z
        .boolean()
        .optional()
        .default(true)
        .describe("Also create overall (extreme-grid) chains on the outer tier."),
      includeOpeningTier: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Innermost exterior tier: continuous openings and wall piers along the facade (doors/windows + wall ends). Default true."
        ),
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
        .describe("Extra overshoot beyond outer tier for bubbles (mm). Omitted = 1.5 tier gaps."),
    },
    async (args) => {
      const params = {
        gridIds: args.gridIds ?? [],
        firstOffsetMm: args.firstOffsetMm,
        tierGapMm: args.tierGapMm,
        includeOverall: args.includeOverall,
        includeOpeningTier: args.includeOpeningTier,
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
              text: JSON.stringify(response),
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
