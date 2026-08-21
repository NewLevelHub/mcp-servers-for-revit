import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetRoomGeometryMetricsTool(server: McpServer) {
  server.tool(
    "get_room_geometry_metrics",
    "Extract room geometry metrics for normative checks: width/depth in millimeters, area in square meters, and inferred corridor width.",
    {
      includeUnplacedRooms: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include unplaced rooms (area=0) in the output."),
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Optional level name filter."),
    },
    async (args) => {
      const params = {
        includeUnplacedRooms: args.includeUnplacedRooms ?? false,
        levelName: args.levelName ?? "",
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_room_geometry_metrics", params);
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
              text: `get_room_geometry_metrics failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
