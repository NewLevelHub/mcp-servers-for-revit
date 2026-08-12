import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetDocumentStylesTool(server: McpServer) {
  server.tool(
    "get_document_styles",
    "Return project annotation and detailing styles: dimension types, grid types, text note types (text height in mm), line patterns, line styles (OST_Lines subcategories with weight, pattern and colour), filled region types, fill patterns (drafting/model hatches), and title block types. Call this before create_detail_lines (lineStyleName), create_detail_regions (filledRegionTypeName / fillPatternName) and create_node_detail. Raw GraphicsStyle entries are omitted by default (can be thousands); lineStyles is the short list you normally want.",
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
