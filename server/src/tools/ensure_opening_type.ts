import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerEnsureOpeningTypeTool(server: McpServer) {
  server.tool(
    "ensure_opening_type",
    "Return a door or window type of an exact size, duplicating sourceTypeId and setting its " +
      "width/height when the project has none within toleranceMm (REV-153). " +
      "Use it when tracing a DWG measures a size the template does not stock " +
      "(789 mm windows, 917 mm doors…) — placing the nearest stock size instead is a silent " +
      "dimensional error in the model. Units: mm.",
    {
      widthMm: z
        .number()
        .positive()
        .describe("Target opening width in mm, as traced from the DWG."),
      heightMm: z
        .number()
        .optional()
        .describe("Target opening height in mm. Omit to keep the source type's height."),
      sourceTypeId: z
        .number()
        .int()
        .positive()
        .describe(
          "FamilySymbol ElementId to duplicate from (get_available_family_types, OST_Doors / OST_Windows). Its family decides what the new type looks like."
        ),
      typeName: z
        .string()
        .optional()
        .describe("Name for the created type. Omit for '<width> x <height> мм'."),
      toleranceMm: z
        .number()
        .optional()
        .describe(
          "How far an existing type may be from the target before a new one is made (default 5)."
        ),
    },
    async (args) => {
      const params: Record<string, unknown> = {
        widthMm: args.widthMm,
        heightMm: args.heightMm ?? 0,
        sourceTypeId: args.sourceTypeId,
        toleranceMm: args.toleranceMm ?? 5,
      };
      if (args.typeName !== undefined) params.typeName = args.typeName;

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("ensure_opening_type", params);
        });

        return {
          content: [{ type: "text" as const, text: JSON.stringify(response) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `ensure_opening_type failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
