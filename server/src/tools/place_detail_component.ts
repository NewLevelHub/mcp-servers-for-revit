import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const pointSchema = z.object({
  x: z.number().describe("X, mm"),
  y: z.number().describe("Y, mm"),
  z: z.number().optional().default(0).describe("Z, mm"),
});

const componentItemSchema = z.object({
  familyName: z
    .string()
    .optional()
    .describe("Detail component family name (get_available_family_types lists them)."),
  typeName: z.string().optional().describe("Detail component type name."),
  point: pointSchema.describe("Placement point (line start for line-based components), model mm."),
  endPoint: pointSchema
    .optional()
    .describe("Line end for line-based detail components, mm. Omit for point-based."),
  rotation: z
    .number()
    .optional()
    .default(0)
    .describe("Rotation around the view direction at the placement point, degrees."),
});

export function registerPlaceDetailComponentTool(server: McpServer) {
  server.tool(
    "place_detail_component",
    "Place 2D detail components (детали узлов: гидроизоляция, утеплитель, крепёж и т.п.) on a detail or drafting view. Supports point-based (point + optional rotation) and line-based (point + endPoint) components resolved by family/type name. When a requested type is missing, the response lists available detail component types in the project. Partial success per item.",
    {
      viewId: z.number().int().optional().describe("Target view element id."),
      viewUniqueId: z.string().optional().describe("Target view uniqueId."),
      viewName: z.string().optional().describe("Target view name."),
      items: z
        .array(componentItemSchema)
        .min(1)
        .describe("Detail components to place."),
    },
    async (args) => {
      const params = {
        viewId: args.viewId ?? 0,
        viewUniqueId: args.viewUniqueId ?? "",
        viewName: args.viewName ?? "",
        items: args.items,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("place_detail_component", params);
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
              text: `Place detail component failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
