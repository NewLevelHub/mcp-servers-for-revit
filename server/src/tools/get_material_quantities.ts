import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetMaterialQuantitiesTool(server: McpServer) {
  server.tool(
    "get_material_quantities",
    "Calculate material quantities and takeoffs from the current Revit project. Returns detailed information about each material including name, class, area, volume, and element counts. Prefer categoryFilters (e.g. OST_Walls, OST_Floors) or selectedElementsOnly=true — unfiltered whole-model takeoff is slow on large projects.",
    {
      categoryFilters: z
        .array(z.string())
        .optional()
        .describe("Recommended. List of Revit category names to filter by (e.g., ['OST_Walls', 'OST_Floors', 'OST_Roofs']). If omitted, all instance elements are scanned (slow on large models)."),
      selectedElementsOnly: z
        .boolean()
        .optional()
        .default(false)
        .describe("Whether to only analyze currently selected elements. Defaults to false (analyze entire project)."),
    },
    async (args, extra) => {
      const params = {
        categoryFilters: args.categoryFilters ?? null,
        selectedElementsOnly: args.selectedElementsOnly ?? false,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_material_quantities", params);
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
              text: `Get material quantities failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
