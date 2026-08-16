import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerTagAllWallsTool(server: McpServer) {
  server.tool(
    "tag_all_walls",
    "Narrow shortcut: tag EVERY wall in the active view, at each wall's middle point. " +
      "No way to choose which walls, which view, or any other category — for that use tag_elements " +
      "(category or elementIds, any category, any view), which is the general tool and covers walls too.",
    {
      useLeader: z
        .boolean()
        .optional()
        .default(false)
        .describe("Whether to use a leader line when creating the tags"),
      tagTypeId: z
        .string()
        .optional()
        .describe("The ID of the specific wall tag family type to use. If not provided, the default wall tag type will be used"),
    },
    async (args, extra) => {
      const params = args;
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("tag_walls", params);
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
              text: `Wall tagging failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
          // toolOutcome normalises thrown errors and JSON refusals; a plain-text
          // failure returned from here would otherwise read as a success.
          isError: true,
        };
      }
    }
  );
}