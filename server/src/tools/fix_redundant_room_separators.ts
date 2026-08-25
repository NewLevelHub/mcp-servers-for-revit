import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerFixRedundantRoomSeparatorsTool(server: McpServer) {
  server.tool(
    "fix_redundant_room_separators",
    "REV-180's one automatic warning fix: deletes room-separation lines Revit has flagged as " +
      "redundant because a wall already overlaps them (the model's own warning list is the source " +
      "of truth — see explain_model_warnings, dangerRank 2, autoFixable: true). Only the separation " +
      "lines are ever touched, never the wall. Defaults to a preview (confirm omitted or false): " +
      "reports what would be removed without changing the model. Pass confirm:true to actually " +
      "delete them, inside a transaction that rolls back whole on any error.",
    {
      confirm: z
        .boolean()
        .optional()
        .default(false)
        .describe("false (default): preview only, nothing is deleted. true: apply the fix for real."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("fix_redundant_room_separators", {
            confirm: args.confirm ?? false,
          });
        });

        return {
          content: [{ type: "text" as const, text: JSON.stringify(response) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `fix_redundant_room_separators не выполнен: ${
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
