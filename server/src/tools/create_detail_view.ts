import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const pointSchema = z.object({
  x: z.number().describe("X, mm"),
  y: z.number().describe("Y, mm"),
  z: z.number().optional().default(0).describe("Z, mm"),
});

export function registerCreateDetailViewTool(server: McpServer) {
  server.tool(
    "create_detail_view",
    "Create a detail view for construction nodes (узлы). Mode 'callout' cuts a detail callout from a parent plan/section view around an element (elementId + padding) or an explicit rectangle (areaMin/areaMax, model mm); mode 'drafting' creates an independent drafting view for drawing a node from scratch. Sets name, scale (e.g. 10 for 1:10), and detail level (Fine by default). By default activates the new view in the UI. Then use create_detail_lines, create_text_note, create_dimensions, and place_detail_component.",
    {
      mode: z
        .enum(["callout", "drafting"])
        .optional()
        .default("callout")
        .describe("callout — detail callout of a model view; drafting — independent drafting view."),
      name: z.string().optional().describe("View name, e.g. 'Узел 1. Примыкание кровли'."),
      scale: z
        .number()
        .int()
        .optional()
        .default(10)
        .describe("View scale denominator: 10 means 1:10. Defaults to 10."),
      detailLevel: z
        .enum(["Coarse", "Medium", "Fine"])
        .optional()
        .default("Fine")
        .describe("Detail level of the created view. Defaults to Fine."),
      activateView: z
        .boolean()
        .optional()
        .default(true)
        .describe("Switch the Revit UI to the created view. Defaults to true."),
      parentViewId: z
        .number()
        .int()
        .optional()
        .describe("Parent view element id (callout mode)."),
      parentViewUniqueId: z.string().optional().describe("Parent view uniqueId (callout mode)."),
      parentViewName: z.string().optional().describe("Parent view name (callout mode)."),
      elementId: z
        .number()
        .int()
        .optional()
        .describe("Element whose bounding box defines the callout area (callout mode)."),
      padding: z
        .number()
        .optional()
        .default(300)
        .describe("Padding around the element bounding box, mm. Defaults to 300."),
      areaMin: pointSchema
        .optional()
        .describe("Explicit callout area corner, model coordinates in mm (callout mode)."),
      areaMax: pointSchema
        .optional()
        .describe("Opposite callout area corner, model coordinates in mm (callout mode)."),
    },
    async (args) => {
      const params = {
        mode: args.mode ?? "callout",
        name: args.name ?? "",
        scale: args.scale ?? 10,
        detailLevel: args.detailLevel ?? "Fine",
        activateView: args.activateView ?? true,
        parentViewId: args.parentViewId ?? 0,
        parentViewUniqueId: args.parentViewUniqueId ?? "",
        parentViewName: args.parentViewName ?? "",
        elementId: args.elementId ?? 0,
        padding: args.padding ?? 300,
        areaMin: args.areaMin ?? null,
        areaMax: args.areaMax ?? null,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_detail_view", params);
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
              text: `Create detail view failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
