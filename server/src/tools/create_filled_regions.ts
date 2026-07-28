import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

/**
 * Annotate → Filled Region («Цветовая область»): paints room contours with a
 * detail filled region type from the project (e.g. ADSK_У_Сплошная_Красный).
 * Not the same as View Color Scheme (color_elements) or room-tag Override.
 */
export function registerCreateFilledRegionsTool(server: McpServer) {
  server.tool(
    "create_filled_regions",
    "Paint rooms on the active floor plan with Annotate → Filled Region («Цветовая область»): closed room boundary → solid fill type from the project (e.g. ADSK_У_Сплошная_Красный). Prefer this for norm-violation area paint that looks like the Annotate UI. Do NOT confuse with color_elements (View Color Scheme / цветовая схема) or highlight_room_tags (tag text only). Pass roomIds and/or roomNames; omit both to paint all rooms on the active view. clearPrevious=true removes prior MCP regions tagged in Comments. clearOnly=true removes prior MCP regions without painting (use for «удали разметку»).",
    {
      roomIds: z
        .array(z.number().int())
        .optional()
        .describe("Revit Room element ids to paint. Omit with roomNames empty to paint all rooms on the active plan."),
      roomNames: z
        .array(z.string())
        .optional()
        .describe("Room names on the active plan (matched case-insensitively), e.g. ['Спальня','Лоджия']."),
      filledRegionTypeName: z
        .string()
        .optional()
        .describe(
          "Exact or partial Filled Region type name from the project. Example: ADSK_У_Сплошная_Красный. If omitted, picks by colorPreset."
        ),
      colorPreset: z
        .enum(["red", "green", "blue", "grey", "gray"])
        .optional()
        .default("red")
        .describe("Used when filledRegionTypeName is omitted — picks a matching Сплошная_* type in the project."),
      clearPrevious: z
        .boolean()
        .optional()
        .default(true)
        .describe("Delete existing Filled Regions on this view whose Comments start with commentTag (default MCP-FR)."),
      clearOnly: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "If true: only remove prior MCP-FR regions on the view; do not paint rooms. Prefer this for «удали разметку» / clear markup."
        ),
      commentTag: z
        .string()
        .optional()
        .default("MCP-FR")
        .describe("Written to Comments on created regions; used to find them on clearPrevious."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_filled_regions", {
            roomIds: args.roomIds ?? [],
            roomNames: args.roomNames ?? [],
            filledRegionTypeName: args.filledRegionTypeName ?? "",
            colorPreset: args.colorPreset ?? "red",
            clearPrevious: args.clearPrevious ?? true,
            clearOnly: args.clearOnly ?? false,
            commentTag: args.commentTag ?? "MCP-FR",
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
              text: `create_filled_regions failed: ${
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
