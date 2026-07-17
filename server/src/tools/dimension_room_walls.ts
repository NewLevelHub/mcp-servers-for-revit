import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerDimensionRoomWallsTool(server: McpServer) {
  server.tool(
    "dimension_room_walls",
    "Create chained wall dimensions (width + depth) for a room on the active floor plan — matches working-doc practice: interior finish-face chains inside the room. Default placement 'interior'. Prefer dimensionType 'ADSK_Основной_2.5 мм' from the project template; empty name auto-picks that style when present. Use placement 'exterior' ONLY on explicit request (axes/facade). Offset in mm.",
    {
      roomId: z
        .number()
        .int()
        .positive()
        .describe("Element ID of the room to dimension"),
      placement: z
        .enum(["interior", "exterior"])
        .optional()
        .default("interior")
        .describe(
          "interior (default) — width and depth chains inside the room; exterior — chains outside the boundary, only on explicit request."
        ),
      offsetMm: z
        .number()
        .positive()
        .optional()
        .default(300)
        .describe(
          "Offset of dimension lines from the room boundary in mm (inward for interior, outward for exterior)"
        ),
      dimensionType: z
        .string()
        .optional()
        .default("")
        .describe(
          "DimensionType name from the project (prefer 'ADSK_Основной_2.5 мм'). Empty auto-picks ADSK main linear type."
        ),
      dimensionStyleId: z
        .number()
        .optional()
        .default(-1)
        .describe("DimensionType element ID. Used when name lookup fails."),
      viewId: z
        .number()
        .optional()
        .default(-1)
        .describe("Floor plan view ID. -1 uses the active view."),
    },
    async (args, extra) => {
      const params = {
        roomId: args.roomId,
        placement: args.placement ?? "interior",
        offsetMm: args.offsetMm,
        dimensionType: args.dimensionType,
        dimensionStyleId: args.dimensionStyleId,
        viewId: args.viewId,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("dimension_room_walls", params);
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
              text: `Room wall dimension creation failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
