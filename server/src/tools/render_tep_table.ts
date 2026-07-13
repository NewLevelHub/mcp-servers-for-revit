import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerRenderTepTableTool(server: McpServer) {
  server.tool(
    "render_tep_table",
    "Render a technical-economic indicators (TEP) table on a sheet (default 'Общие данные'). Column layout, headings, widths, and alignment are replicated from a reference schedule (see get_schedule_definition, e.g. 'О_АР_Квартиры_ТЭП' or 'ADSK_О_С_С'); text sizes come from project text note types (see get_document_styles); values come from export_tep_data (units mm/m²/m³). Draws text notes and detail grid lines directly on the sheet, creating it when missing.",
    {
      templateScheduleName: z
        .string()
        .optional()
        .describe(
          "Name of the reference schedule whose column structure the table should replicate, e.g. 'О_АР_Квартиры_ТЭП'. When omitted or not found, standard TEP columns are used (№ п/п / Наименование / Ед. изм. / Кол-во)."
        ),
      sheetName: z
        .string()
        .optional()
        .default("Общие данные")
        .describe("Target sheet name. Defaults to 'Общие данные'."),
      sheetNumber: z
        .string()
        .optional()
        .describe("Sheet number to assign when the sheet has to be created."),
      createSheetIfMissing: z
        .boolean()
        .optional()
        .default(true)
        .describe("Create the sheet when it does not exist. Defaults to true."),
      title: z
        .string()
        .optional()
        .default("Технико-экономические показатели")
        .describe("Table title drawn above the header row."),
      positionX: z
        .number()
        .optional()
        .default(20)
        .describe("Offset of the table's top-left corner from the sheet's left edge, mm."),
      positionY: z
        .number()
        .optional()
        .default(20)
        .describe("Offset of the table's top-left corner from the sheet's top edge, mm."),
      rowHeight: z
        .number()
        .optional()
        .default(8)
        .describe("Row height in mm. Defaults to 8."),
      titleTextTypeName: z
        .string()
        .optional()
        .describe(
          "TextNoteType name for the title row (list available types with get_document_styles). Defaults to the project default text type."
        ),
      headerTextTypeName: z
        .string()
        .optional()
        .describe(
          "TextNoteType name for the header row and group rows. Defaults to the project default text type."
        ),
      bodyTextTypeName: z
        .string()
        .optional()
        .describe(
          "TextNoteType name for data rows. Defaults to the project default text type."
        ),
      includeLevels: z
        .boolean()
        .optional()
        .default(true)
        .describe("Append per-level area rows. Defaults to true."),
      includeRoomsByPurpose: z
        .boolean()
        .optional()
        .default(true)
        .describe("Append rows for rooms grouped by purpose (department). Defaults to true."),
      includeUnplacedRooms: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include unplaced rooms in TEP aggregation. Defaults to false."),
      includeNotEnclosedRooms: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include not fully enclosed rooms in TEP aggregation. Defaults to false."),
    },
    async (args) => {
      const params = {
        templateScheduleName: args.templateScheduleName ?? "",
        sheetName: args.sheetName ?? "Общие данные",
        sheetNumber: args.sheetNumber ?? "",
        createSheetIfMissing: args.createSheetIfMissing ?? true,
        title: args.title ?? "Технико-экономические показатели",
        positionX: args.positionX ?? 20,
        positionY: args.positionY ?? 20,
        rowHeight: args.rowHeight ?? 8,
        titleTextTypeName: args.titleTextTypeName ?? "",
        headerTextTypeName: args.headerTextTypeName ?? "",
        bodyTextTypeName: args.bodyTextTypeName ?? "",
        includeLevels: args.includeLevels ?? true,
        includeRoomsByPurpose: args.includeRoomsByPurpose ?? true,
        includeUnplacedRooms: args.includeUnplacedRooms ?? false,
        includeNotEnclosedRooms: args.includeNotEnclosedRooms ?? false,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("render_tep_table", params);
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
              text: `Render TEP table failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
