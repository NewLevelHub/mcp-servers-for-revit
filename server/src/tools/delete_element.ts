import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerDeleteElementTool(server: McpServer) {
  server.tool(
    "delete_element",
    "Delete elements from the Revit model by id. The result names what was targeted (requested) and how many extra dependents Revit removed with them (dependentsRemoved) — deleting a sheet also drops its viewports and title block, so count is normally larger than the id list. Ids listed in missingIds are already gone; never call again with them.",
    {
      elementIds: z
        .array(z.string())
        .describe("The IDs of the elements to delete"),
    },
    async (args) => {
      const params = {
        elementIds: args.elementIds,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("delete_element", params);
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
              text: `delete element failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
