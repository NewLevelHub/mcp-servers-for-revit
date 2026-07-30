import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerExportRoomDataTool(server: McpServer) {
  server.tool(
    "export_room_data",
    "Export room data from the Revit project. Returns ElementIds, names, numbers, levels, areas (m²), etc. " +
      "For «сколько помещений на этаже» use filterByActiveView=true or levelName — otherwise all project rooms are returned.",
    {
      includeUnplacedRooms: z
        .boolean()
        .optional()
        .default(false)
        .describe("Whether to include unplaced rooms (rooms not yet placed in the model). Defaults to false."),
      includeNotEnclosedRooms: z
        .boolean()
        .optional()
        .default(false)
        .describe("Whether to include rooms that are not fully enclosed. Defaults to false."),
      filterByActiveView: z
        .boolean()
        .optional()
        .default(false)
        .describe("When true, only rooms on the active floor plan level. Use for «на этаже» queries."),
      levelName: z
        .string()
        .optional()
        .describe("Filter by level name (e.g. «2 этаж»). Ignored when filterByActiveView resolves a level."),
      levelId: z
        .number()
        .optional()
        .describe("Filter by level ElementId. Takes precedence over levelName when set."),
    },
    async (args, extra) => {
      const params = {
        includeUnplacedRooms: args.includeUnplacedRooms ?? false,
        includeNotEnclosedRooms: args.includeNotEnclosedRooms ?? false,
        filterByActiveView: args.filterByActiveView ?? false,
        ...(args.levelName ? { levelName: args.levelName } : {}),
        ...(args.levelId != null ? { levelId: args.levelId } : {}),
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("export_room_data", params);
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
              text: `Export room data failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
