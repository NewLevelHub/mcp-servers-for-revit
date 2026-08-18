import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCreateGridTool(server: McpServer) {
  server.tool(
    "create_grid",
    "Create coordination grids in Revit. PREFERRED: autoFromWalls=true — places grids on load-bearing wall centerlines with extents beyond the building. Bubbles DEFAULT bottomLeft (numbers below, letters left — one end only). After grids exist, use dimension_grids for exterior axial chains offset from the full building envelope. Naming: numeric (1,2,3), cyrillic (А,Б,В…). All units mm.",
    {
      autoFromWalls: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "PREFERRED: place grids on structural/bearing wall centerlines of the active (or levelName) floor. Ignores count/spacing. Use this for working documentation."
        ),
      wallFilter: z
        .enum(["structural", "exterior", "all"])
        .optional()
        .default("structural")
        .describe("Wall filter for autoFromWalls. Default structural = concrete/bearing/thick walls."),
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Level for autoFromWalls. Empty = active floor plan level."),
      minWallThicknessMm: z
        .number()
        .optional()
        .default(400)
        .describe("Min wall thickness (mm) for autoFromWalls. Default 400 — keeps ~500 mm concrete cores, skips t=200."),
      clusterToleranceMm: z
        .number()
        .optional()
        .default(280)
        .describe("Merge wall centerlines closer than this (mm). Default 280 merges face/center location-line duplicates."),
      extentOvershootMm: z
        .number()
        .optional()
        .default(4000)
        .describe(
          "How far grid lines extend beyond the wall bbox (mm). Default 4000 — room for 2 exterior dimension tiers + bubbles outside."
        ),
      autoComputeExtents: z
        .boolean()
        .optional()
        .describe("Recompute extents from walls/positions. Defaults true for autoFromWalls."),
      xPositionsMm: z
        .array(z.number())
        .optional()
        .default([])
        .describe("Explicit X positions (mm) for vertical grids. Overrides xCount/xSpacing when set."),
      yPositionsMm: z
        .array(z.number())
        .optional()
        .default([])
        .describe("Explicit Y positions (mm) for horizontal grids. Overrides yCount/ySpacing when set."),
      xCount: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("Number of X (vertical) grids — only when not using autoFromWalls / xPositionsMm"),
      xSpacing: z
        .number()
        .positive()
        .optional()
        .describe("Spacing between X grids in mm — only for uniform spacing mode"),
      xStartLabel: z
        .string()
        .default("1")
        .describe("Starting label for X-axis grids (e.g. '1' or 'А')"),
      xNamingStyle: z
        .enum(["alphabetic", "numeric", "cyrillic"])
        .default("numeric")
        .describe("X naming: numeric (1,2,3), cyrillic (А,Б,В), alphabetic (A,B,C). RU projects: numeric for X."),
      yCount: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("Number of Y (horizontal) grids — only when not using autoFromWalls / yPositionsMm"),
      ySpacing: z
        .number()
        .positive()
        .optional()
        .describe("Spacing between Y grids in mm — only for uniform spacing mode"),
      yStartLabel: z
        .string()
        .default("А")
        .describe("Starting label for Y-axis grids (e.g. 'А' or 'A')"),
      yNamingStyle: z
        .enum(["alphabetic", "numeric", "cyrillic"])
        .default("cyrillic")
        .describe("Y naming. RU projects: cyrillic (А,Б,В,Г,Д…)."),
      xExtentMin: z
        .number()
        .default(0)
        .describe("Min X extent mm (auto-filled when autoFromWalls)"),
      xExtentMax: z
        .number()
        .default(50000)
        .describe("Max X extent mm (auto-filled when autoFromWalls)"),
      yExtentMin: z
        .number()
        .default(0)
        .describe("Min Y extent mm (auto-filled when autoFromWalls)"),
      yExtentMax: z
        .number()
        .default(50000)
        .describe("Max Y extent mm (auto-filled when autoFromWalls)"),
      elevation: z
        .number()
        .default(0)
        .describe("Elevation for grid lines in mm (Z)"),
      xStartPosition: z
        .number()
        .default(0)
        .describe("Start X for uniform spacing mode (mm)"),
      yStartPosition: z
        .number()
        .default(0)
        .describe("Start Y for uniform spacing mode (mm)"),
      gridTypeName: z
        .string()
        .optional()
        .default("")
        .describe("GridType name from the project for bubble style"),
      gridTypeId: z
        .number()
        .optional()
        .default(-1)
        .describe("GridType element ID from the project"),
      configureDisplayOnAllPlans: z
        .boolean()
        .optional()
        .default(true)
        .describe("Configure 2D extents and bubbles on all floor plans after creation"),
      showBubbles: z
        .boolean()
        .optional()
        .default(true)
        .describe("Show grid bubbles when display is configured"),
      bubbleEnd: z
        .enum(["both", "start", "end", "bottomLeft", "topRight"])
        .optional()
        .default("bottomLeft")
        .describe(
          "Which end shows the bubble. DEFAULT bottomLeft = numbers below, letters to the left (one end only). Use 'both' only if explicitly requested."
        ),
    },
    async (args) => {
      const params = {
        autoFromWalls: args.autoFromWalls,
        wallFilter: args.wallFilter,
        levelName: args.levelName,
        minWallThicknessMm: args.minWallThicknessMm,
        clusterToleranceMm: args.clusterToleranceMm,
        extentOvershootMm: args.extentOvershootMm,
        autoComputeExtents: args.autoComputeExtents,
        xPositionsMm: args.xPositionsMm ?? [],
        yPositionsMm: args.yPositionsMm ?? [],
        xCount: args.xCount,
        xSpacing: args.xSpacing,
        xStartLabel: args.xStartLabel,
        xNamingStyle: args.xNamingStyle,
        yCount: args.yCount,
        ySpacing: args.ySpacing,
        yStartLabel: args.yStartLabel,
        yNamingStyle: args.yNamingStyle,
        xExtentMin: args.xExtentMin,
        xExtentMax: args.xExtentMax,
        yExtentMin: args.yExtentMin,
        yExtentMax: args.yExtentMax,
        elevation: args.elevation,
        xStartPosition: args.xStartPosition,
        yStartPosition: args.yStartPosition,
        gridTypeName: args.gridTypeName,
        gridTypeId: args.gridTypeId,
        configureDisplayOnAllPlans: args.configureDisplayOnAllPlans,
        showBubbles: args.showBubbles,
        bubbleEnd: args.bubbleEnd,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_grid", params);
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
              text: `Create grid failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
