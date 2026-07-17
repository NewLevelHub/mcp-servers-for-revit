import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetOpeningGeometryInfoTool(server: McpServer) {
  server.tool(
    "get_opening_geometry_info",
    "Extract window sill height (INSTANCE_SILL_HEIGHT) and door/window opening " +
      "height (DOOR_HEIGHT / WINDOW_HEIGHT) for normative checks (REV-58). " +
      "Excludes откосы and sill accessories. Optional level filter.",
    {
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Optional level name filter."),
    },
    async (args) => {
      const params = {
        levelName: args.levelName ?? "",
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(
            "get_opening_geometry_info",
            params
          );
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
              text: `get_opening_geometry_info failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
