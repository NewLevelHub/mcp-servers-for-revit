import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const sheetSchema = z.object({
  sheetNumber: z.string().describe("Sheet number (e.g. A101)"),
  sheetName: z.string().describe("Sheet name"),
  titleBlockTypeId: z.number().optional().default(0),
  titleBlockFamilyName: z
    .string()
    .optional()
    .default("")
    .describe("Title block family name from the project"),
  titleBlockTypeName: z
    .string()
    .optional()
    .default("")
    .describe("Title block type name from the project"),
  sheetFormat: z
    .string()
    .optional()
    .default("")
    .describe(
      'Paper format "A0".."A4". ADSK "ОсновнаяНадпись" is one family at every size and picks the frame from its "Формат А" parameter, so ask for the format here instead of guessing a type name.'
    ),
  revisionIds: z.array(z.number()).optional().default([]),
  parameters: z.record(z.unknown()).optional(),
});

export function registerCreateSheetTool(server: McpServer) {
  server.tool(
    "create_sheet",
    "Create a Revit working sheet with a title block from the current project. Views and schedules are placed afterwards with place_view_on_sheet. Leave the title block unset to get the project's основная надпись (working stamp) and pass sheetFormat for the size — never request an ADSK_Титул type, that is the cover page.",
    {
      sheet: sheetSchema.describe(
        "Sheet settings. Prefer sheetFormat (e.g. \"A3\") over naming a title block type; titleBlockFamilyName/titleBlockTypeName/titleBlockTypeId only when a specific stamp is required."
      ),
    },
    async (args) => {
      const params = { sheet: args.sheet };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_sheet", params);
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
              text: `Create sheet failed: ${
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
