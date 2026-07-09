import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetDoorEgressInfoTool(server: McpServer) {
  server.tool(
    "get_door_egress_info",
    "Extract door opening width, family/type, host wall, and egress-path hint flags for normative checks.",
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
          return await revitClient.sendCommand("get_door_egress_info", params);
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
              text: `get_door_egress_info failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
