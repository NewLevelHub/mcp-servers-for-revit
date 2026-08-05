import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetCadLinkGeometryTool(server: McpServer) {
  server.tool(
    "get_cad_link_geometry",
    "Read line/arc/polyline geometry from a DWG/CAD ImportInstance (link or import) " +
      "on the active floor plan. Returns segments in mm { startMm, endMm, layer, cadId } " +
      "plus bbox — use before tracing walls from CAD (REV-138). " +
      "Fail-fast if no CAD is visible on the view.",
    {
      cadLinkName: z
        .string()
        .optional()
        .describe(
          "Optional CAD link/import name filter (substring, case-insensitive)."
        ),
      layerFilter: z
        .union([z.string(), z.array(z.string())])
        .optional()
        .describe(
          "Optional DWG layer name or list (substring match). Example: 'A-WALL' or ['WALL','Оси']."
        ),
      viewId: z
        .number()
        .optional()
        .describe("Optional view element id; default = active view."),
      minLengthMm: z
        .number()
        .optional()
        .default(0)
        .describe("Skip segments shorter than this length (mm). Default 0."),
      limit: z
        .number()
        .optional()
        .default(5000)
        .describe("Max segments to return (default 5000)."),
    },
    async (args) => {
      const params: Record<string, unknown> = {
        cadLinkName: args.cadLinkName ?? "",
        minLengthMm: args.minLengthMm ?? 0,
        limit: args.limit ?? 5000,
      };
      if (args.layerFilter !== undefined) {
        params.layerFilter = args.layerFilter;
      }
      if (args.viewId !== undefined) {
        params.viewId = args.viewId;
      }

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_cad_link_geometry", params);
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
              text: `get_cad_link_geometry failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
