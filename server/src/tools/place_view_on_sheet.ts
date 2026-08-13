import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const placementSchema = z.object({
  sheetId: z.number().optional().default(0),
  sheetUniqueId: z.string().optional().default(""),
  viewId: z.number().optional().default(0),
  viewUniqueId: z.string().optional().default(""),
  positionX: z
    .number()
    .describe(
      "X of the lower-left corner, in millimeters from the lower-left corner of the printed frame (title block extents)"
    ),
  positionY: z
    .number()
    .describe(
      "Y of the lower-left corner, in millimeters from the lower-left corner of the printed frame (title block extents)"
    ),
  viewportTypeId: z.number().optional().default(0),
  displayTitle: z.boolean().optional(),
  scaleOverride: z.number().optional().default(0),
  labelText: z.string().optional().default(""),
  rotation: z.number().optional().default(0),
  parameters: z.record(z.unknown()).optional(),
});

export function registerPlaceViewOnSheetTool(server: McpServer) {
  server.tool(
    "place_view_on_sheet",
    "Place a floor plan, view, or schedule on a sheet. Positions are the lower-left corner in mm from the printed frame corner; the placement is always kept inside the frame and clear of the ГОСТ stamp (185×55 mm, bottom-right), so a sane position like x=20 y=70 is enough. Returns warnings when the content is larger than the printable field — read them instead of retrying with other coordinates.",
    {
      placement: placementSchema.describe(
        "Placement settings. Provide sheetId or sheetUniqueId and viewId or viewUniqueId."
      ),
    },
    async (args) => {
      const params = { placement: args.placement };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("place_view_on_sheet", params);
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
              text: `Place view on sheet failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
