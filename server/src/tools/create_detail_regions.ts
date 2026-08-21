import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const pointSchema = z.object({
  x: z.number().describe("X, mm"),
  y: z.number().describe("Y, mm"),
});

const regionSchema = z.object({
  points: z
    .array(pointSchema)
    .min(3)
    .describe("Outer contour in view plane, mm. The closing point may be omitted."),
  holes: z
    .array(z.array(pointSchema).min(3))
    .optional()
    .describe("Inner contours cut out of the region, mm."),
  filledRegionTypeName: z
    .string()
    .optional()
    .describe("Existing filled region type by name; takes precedence over fillPatternName."),
  fillPatternName: z
    .string()
    .optional()
    .describe(
      "Hatch by fill pattern name (e.g. Бетон, Diagonal crosshatch). When no filled region type draws with it, one is duplicated unless createMissingTypes is false."
    ),
  label: z.string().optional().describe("Written to Comments next to the tag, for later identification."),
});

export function registerCreateDetailRegionsTool(server: McpServer) {
  server.tool(
    "create_detail_regions",
    "Hatch arbitrary contours on a drafting, detail, section or plan view (layer hatches of a node, insulation, concrete). Coordinates are mm in the view plane; holes are cut out of the outer contour. Pick names with get_document_styles (filledRegionTypes / fillPatterns). This is the detailing counterpart of create_filled_regions, which paints Room boundaries for norm audits — use that one for violations, this one for details.",
    {
      viewId: z.number().int().optional().describe("Target view element id."),
      viewUniqueId: z.string().optional().describe("Target view uniqueId."),
      viewName: z.string().optional().describe("Target view name."),
      regions: z.array(regionSchema).min(1).describe("Contours to hatch."),
      filledRegionTypeName: z
        .string()
        .optional()
        .describe("Fallback filled region type for regions that name neither a type nor a pattern."),
      createMissingTypes: z
        .boolean()
        .optional()
        .describe(
          "Duplicate a filled region type when the requested hatch pattern has none. Default true."
        ),
      clearPrevious: z
        .boolean()
        .optional()
        .describe("Delete regions previously created by this tool on the view first. Default false."),
      clearOnly: z.boolean().optional().describe("Delete previous regions and create nothing."),
      commentTag: z.string().optional().describe("Comments tag used for clearPrevious. Default MCP-DR."),
    },
    async (args) => {
      const params = {
        viewId: args.viewId ?? 0,
        viewUniqueId: args.viewUniqueId ?? "",
        viewName: args.viewName ?? "",
        regions: (args.regions ?? []).map((region) => ({
          points: region.points,
          holes: region.holes ?? [],
          filledRegionTypeName: region.filledRegionTypeName ?? "",
          fillPatternName: region.fillPatternName ?? "",
          label: region.label ?? "",
        })),
        filledRegionTypeName: args.filledRegionTypeName ?? "",
        createMissingTypes: args.createMissingTypes ?? true,
        clearPrevious: args.clearPrevious ?? false,
        clearOnly: args.clearOnly ?? false,
        commentTag: args.commentTag ?? "",
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_detail_regions", params);
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
              text: `Create detail regions failed: ${
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
