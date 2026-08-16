import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetSelectedElementsTool(server: McpServer) {
  server.tool(
    "get_selected_elements",
    "Get elements currently selected in Revit. Paginated: the reply carries TotalCount (the whole " +
      "selection), HasMore, Offset and Limit alongside the page in Response. When HasMore is true the " +
      "list is NOT the whole selection — read the next page with offset before acting on it.",
    {
      limit: z
        .number()
        .optional()
        .describe("Page size — maximum number of elements to return (default 100)."),
      offset: z
        .number()
        .optional()
        .describe("Number of selected elements to skip before the page. Use when HasMore is true."),
    },
    async (args, extra) => {
      const params = {
        limit: args.limit || 100,
        offset: args.offset || 0,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_selected_elements", params);
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
              text: `get selected elements failed: ${
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
