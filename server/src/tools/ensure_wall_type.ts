import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerEnsureWallTypeTool(server: McpServer) {
  server.tool(
    "ensure_wall_type",
    "Return a wall type of an exact thickness, duplicating sourceTypeId and widening its " +
      "structural layer when the project has none within toleranceMm (REV-154). " +
      "Use it when tracing a DWG measures a thickness the template does not stock " +
      "(193 mm, 147 mm, 406 mm…) — placing the nearest stock type instead redraws the " +
      "building at the wrong thickness. Units: mm.",
    {
      thicknessMm: z
        .number()
        .positive()
        .describe("Target wall thickness in mm, as measured between the DWG faces."),
      sourceTypeId: z
        .number()
        .int()
        .positive()
        .describe(
          "WallType ElementId to duplicate from (get_available_family_types, categoryList=[OST_Walls]). Its layers decide the construction, so pass a type of the right build-up."
        ),
      typeName: z
        .string()
        .optional()
        .describe("Name for the created type. Omit for '<source> <thickness>мм'."),
      toleranceMm: z
        .number()
        .optional()
        .describe(
          "How far an existing type may be from the target before a new one is made (default 5)."
        ),
    },
    async (args) => {
      const params: Record<string, unknown> = {
        thicknessMm: args.thicknessMm,
        sourceTypeId: args.sourceTypeId,
        toleranceMm: args.toleranceMm ?? 5,
      };
      if (args.typeName !== undefined) params.typeName = args.typeName;

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("ensure_wall_type", params);
        });

        return {
          content: [{ type: "text" as const, text: JSON.stringify(response) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `ensure_wall_type failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
