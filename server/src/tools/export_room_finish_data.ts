import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerExportRoomFinishDataTool(server: McpServer) {
  server.tool(
    "export_room_finish_data",
    "Export room finish data from the current Revit project (read-only). For each room returns roomId, floorFinish, wallFinish, ceilingFinish (explicit null + warning when empty). Boundary face materials with areas in m² are opt-in via includeMaterials — this runs full spatial geometry per room and is slow on large models; combine it with levelName or offset/limit pagination. Response reports totalRooms/returnedRooms/hasMore for paging. Foundation for create_finish_schedule workflows.",
    {
      includeUnplacedRooms: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Whether to include unplaced rooms (rooms not yet placed in the model). Defaults to false."
        ),
      includeNotEnclosedRooms: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Whether to include rooms that are not fully enclosed. Defaults to false."
        ),
      includeMaterials: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Extract boundary face materials per room (expensive spatial geometry; on large models use levelName or offset/limit with it). Defaults to false."
        ),
      levelName: z
        .string()
        .optional()
        .describe("Only rooms on this level (exact name, case-insensitive)."),
      offset: z
        .number()
        .int()
        .min(0)
        .optional()
        .default(0)
        .describe("Rooms to skip (pagination). Rooms are ordered by level elevation, then number."),
      limit: z
        .number()
        .int()
        .min(0)
        .optional()
        .default(0)
        .describe("Max rooms to return; 0 returns all. Use with offset for pagination."),
    },
    async (args) => {
      const params = {
        includeUnplacedRooms: args.includeUnplacedRooms ?? false,
        includeNotEnclosedRooms: args.includeNotEnclosedRooms ?? false,
        includeMaterials: args.includeMaterials ?? false,
        levelName: args.levelName ?? "",
        offset: args.offset ?? 0,
        limit: args.limit ?? 0,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("export_room_finish_data", params);
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
              text: `Export room finish data failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
