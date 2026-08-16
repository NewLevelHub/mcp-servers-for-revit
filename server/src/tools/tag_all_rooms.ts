import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerTagAllRoomsTool(server: McpServer) {
  server.tool(
    "tag_all_rooms",
    "Narrow shortcut: tag rooms in the active view (all of them, or the roomIds given) with the room " +
      "name and number at each room's centre. Room-only and active-view-only — for any other category, " +
      "or to tag in a named view, use tag_elements, which is the general tool and covers rooms too.",
    {
      useLeader: z
        .boolean()
        .optional()
        .default(false)
        .describe("Whether to use a leader line when creating the tags"),
      tagTypeId: z
        .string()
        .optional()
        .describe("The ID of the specific room tag family type to use. If not provided, the default room tag type will be used"),
      roomIds: z
        .array(z.number())
        .optional()
        .describe("Optional array of specific room element IDs to tag. If not provided, all rooms in the current view will be tagged"),
    },
    async (args, extra) => {
      const params = args;
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("tag_rooms", params);
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
              text: `Room tagging failed: ${
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
