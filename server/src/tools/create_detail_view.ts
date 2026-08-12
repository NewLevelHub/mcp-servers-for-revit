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
    "Create a detail view for construction nodes (узлы). Mode 'callout' cuts a detail callout from a parent plan/section view around an element (elementId + padding) or an explicit rectangle (areaMin/areaMax, model mm); mode 'drafting' creates an independent drafting view for drawing a node from scratch; mode 'section' cuts a real section through the model (sectionStart/sectionEnd in mm, or elementId to cut across), where Revit draws the compound layers and material hatches itself. Sets name, scale (e.g. 10 for 1:10), and detail level (Fine by default). By default activates the new view in the UI. Then use create_detail_lines, create_detail_regions, create_text_note, create_dimensions, and place_detail_component — or create_node_detail to generate a whole node from a wall/floor build-up.",
    {
      mode: z
        .enum(["callout", "drafting", "section"])
        .optional()
        .default("callout")
        .describe(
          "callout — detail callout of a model view; drafting — independent drafting view; section — a real cut through the model."
        ),
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
      sectionStart: pointSchema
        .optional()
        .describe("Start of the cutting line, model mm (section mode). Use with sectionEnd."),
      sectionEnd: pointSchema
        .optional()
        .describe("End of the cutting line, model mm (section mode)."),
      sectionBottomMm: z
        .number()
        .optional()
        .describe("Bottom of the cut, model mm (section mode). Default 0."),
      sectionTopMm: z
        .number()
        .optional()
        .describe("Top of the cut, model mm (section mode). Defaults to 3000 above the bottom."),
      sectionDepthMm: z
        .number()
        .optional()
        .describe("How far in front of the cut plane stays visible, mm (section mode). Default 2000."),
      sectionAlongX: z
        .boolean()
        .optional()
        .describe("With elementId in section mode: cut along X (default) or along Y."),
      flip: z
        .boolean()
        .optional()
        .describe(
          "Look at the other side of the cutting line (section mode). The resulting lookDirection is returned so a wrong side is visible."
        ),
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
        sectionStart: args.sectionStart ?? null,
        sectionEnd: args.sectionEnd ?? null,
        sectionBottomMm: args.sectionBottomMm ?? 0,
        sectionTopMm: args.sectionTopMm ?? 0,
        sectionDepthMm: args.sectionDepthMm ?? 2000,
        sectionAlongX: args.sectionAlongX ?? true,
        flip: args.flip ?? false,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_detail_view", params);
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
