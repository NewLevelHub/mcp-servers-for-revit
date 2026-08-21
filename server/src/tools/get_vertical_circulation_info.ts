import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetVerticalCirculationInfoTool(server: McpServer) {
  server.tool(
    "get_vertical_circulation_info",
    "Extract stair width / riser / tread, ramp width / slope%, railing height " +
      "for normative checks (REV-59). Optional level filter.",
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
            "get_vertical_circulation_info",
            params
          );
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
              text: `get_vertical_circulation_info failed: ${
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
