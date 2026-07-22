import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetDocumentStylesTool(server: McpServer) {
  server.tool(
    "get_document_styles",
    "Return project annotation styles for AI-assisted drafting: dimension types, grid types, text note types (text height in mm), line patterns, and title block types. Graphics line styles are omitted by default (large); pass includeGraphicsStyles=true to include them.",
    {
      includeGraphicsStyles: z
        .boolean()
        .optional()
        .describe(
          "Include GraphicsStyle entries (can be thousands). Default false for faster responses."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_document_styles", {
            includeGraphicsStyles: args.includeGraphicsStyles ?? false,
          });
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
              text: `get document styles failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
