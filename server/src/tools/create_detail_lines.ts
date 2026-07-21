import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const pointSchema = z.object({
  x: z.number().describe("X, mm"),
  y: z.number().describe("Y, mm"),
});

const polylineSchema = z.object({
  points: z
    .array(pointSchema)
    .min(2)
    .describe("Polyline vertices in view plane, mm. At least 2 points."),
});

export function registerCreateDetailLinesTool(server: McpServer) {
  server.tool(
    "create_detail_lines",
    "Draw polylines as detail curves on a floor plan, detail callout, or drafting view (node layer sketch, evacuation routes, etc.). Coordinates are in mm in the view plane. Target view via viewId/viewUniqueId/viewName; if omitted, uses the active view. Use after create_detail_view for REV-40 node pilots.",
    {
      viewId: z.number().int().optional().describe("Target view element id."),
      viewUniqueId: z.string().optional().describe("Target view uniqueId."),
      viewName: z.string().optional().describe("Target view name."),
      polylines: z
        .array(polylineSchema)
        .min(1)
        .describe("Polylines to draw; each segment between consecutive points becomes a detail line."),
    },
    async (args) => {
      const params = {
        viewId: args.viewId ?? 0,
        viewUniqueId: args.viewUniqueId ?? "",
        viewName: args.viewName ?? "",
        polylines: args.polylines,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_detail_lines", params);
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
              text: `Create detail lines failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
