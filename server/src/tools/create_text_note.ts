import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const pointSchema = z.object({
  x: z.number().describe("X, mm"),
  y: z.number().describe("Y, mm"),
  z: z.number().optional().default(0).describe("Z, mm"),
});

export function registerCreateTextNoteTool(server: McpServer) {
  server.tool(
    "create_text_note",
    "Create a text note (annotation / выноска) on a view — used for node annotations on detail and drafting views. Position in model/view coordinates (mm), text type by name (see get_document_styles), optional wrapping width, and an optional straight leader pointing at leaderEnd.",
    {
      viewId: z.number().int().optional().describe("Target view element id."),
      viewUniqueId: z.string().optional().describe("Target view uniqueId."),
      viewName: z.string().optional().describe("Target view name."),
      text: z.string().min(1).describe("Note text."),
      position: pointSchema.describe("Text position, mm."),
      textTypeName: z
        .string()
        .optional()
        .describe("TextNoteType name (see get_document_styles). Defaults to the project default."),
      width: z
        .number()
        .optional()
        .default(0)
        .describe("Wrapping width, mm; 0 places an unwrapped note. Defaults to 0."),
      leaderEnd: pointSchema
        .optional()
        .describe("Leader arrow end point, mm; a straight leader is added when provided."),
    },
    async (args) => {
      const params = {
        viewId: args.viewId ?? 0,
        viewUniqueId: args.viewUniqueId ?? "",
        viewName: args.viewName ?? "",
        text: args.text,
        position: args.position,
        textTypeName: args.textTypeName ?? "",
        width: args.width ?? 0,
        leaderEnd: args.leaderEnd ?? null,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_text_note", params);
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
              text: `Create text note failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
