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
  closed: z
    .boolean()
    .optional()
    .describe("Close the contour back to the first point. Default false."),
  lineStyleName: z
    .string()
    .optional()
    .describe("Line style for this polyline; overrides the call-level lineStyleName."),
});

const arcSchema = z.object({
  start: pointSchema.describe("Arc start, mm."),
  end: pointSchema.describe("Arc end, mm."),
  pointOnArc: pointSchema.describe("Any point lying on the arc between start and end, mm."),
  lineStyleName: z.string().optional().describe("Line style for this arc."),
});

export function registerCreateDetailLinesTool(server: McpServer) {
  server.tool(
    "create_detail_lines",
    "Draw polylines and arcs as detail curves on a floor plan, detail callout, section, or drafting view (node layer sketch, evacuation routes, etc.). Coordinates are in mm in the view plane. Line styles are OST_Lines subcategories — list them with get_document_styles (lineStyles); an unknown name falls back to the view default and is reported in availableLineStyles. Target view via viewId/viewUniqueId/viewName; if omitted, uses the active view.",
    {
      viewId: z.number().int().optional().describe("Target view element id."),
      viewUniqueId: z.string().optional().describe("Target view uniqueId."),
      viewName: z.string().optional().describe("Target view name."),
      polylines: z
        .array(polylineSchema)
        .optional()
        .describe("Polylines to draw; each segment between consecutive points becomes a detail line."),
      arcs: z
        .array(arcSchema)
        .optional()
        .describe("Arcs through three points (start, end, and a point on the arc)."),
      lineStyleName: z
        .string()
        .optional()
        .describe("Default line style for everything drawn by this call, e.g. «Тонкие линии»."),
    },
    async (args) => {
      const params = {
        viewId: args.viewId ?? 0,
        viewUniqueId: args.viewUniqueId ?? "",
        viewName: args.viewName ?? "",
        polylines: (args.polylines ?? []).map((polyline) => ({
          points: polyline.points,
          closed: polyline.closed ?? false,
          lineStyleName: polyline.lineStyleName ?? "",
        })),
        arcs: (args.arcs ?? []).map((arc) => ({
          start: arc.start,
          end: arc.end,
          pointOnArc: arc.pointOnArc,
          lineStyleName: arc.lineStyleName ?? "",
        })),
        lineStyleName: args.lineStyleName ?? "",
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_detail_lines", params);
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
