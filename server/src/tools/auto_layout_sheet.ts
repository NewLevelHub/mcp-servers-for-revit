import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const layoutItemSchema = z.object({
  viewId: z.number().int().optional().describe("Element id of the view or schedule."),
  viewUniqueId: z.string().optional().describe("UniqueId of the view or schedule."),
  viewName: z
    .string()
    .optional()
    .describe("View or schedule name, e.g. 'Level 1' or 'Ведомость отделки помещений'."),
});

export function registerAutoLayoutSheetTool(server: McpServer) {
  server.tool(
    "auto_layout_sheet",
    "Automatically lay out views and schedules on a Revit sheet without manual coordinates. Measures the actual extents of each element (Viewport.GetBoxOutline / ScheduleSheetInstance bounding box) and packs them in rows with configurable gaps inside the usable area (sheet outline minus margins and the title-block zone measured from real title-block instances — including bottom + right reserve for GOST Form 3). Title blocks are always treated as obstacles. Oversized viewports are auto-scaled down when autoFitScale is true (increase 1:N until they fit, up to maxViewScale). Schedules cannot be scaled — oversized schedules are skipped with a warning. Views already on another sheet are duplicated as dependents by default (createDependentViewIfNeeded). Success=false when nothing placeable fits; PartialSuccess when only some items place.",
    {
      items: z
        .array(layoutItemSchema)
        .min(1)
        .describe(
          "Views and schedules to place, in the requested order. Each item needs viewId, viewUniqueId, or viewName."
        ),
      sheetId: z.number().int().optional().describe("Element id of the target sheet."),
      sheetUniqueId: z.string().optional().describe("UniqueId of the target sheet."),
      sheetNumber: z
        .string()
        .optional()
        .describe("Sheet number to find the sheet by, or to assign when the sheet is created."),
      sheetName: z
        .string()
        .optional()
        .describe("Sheet name to find the sheet by, or to assign when the sheet is created."),
      createSheetIfMissing: z
        .boolean()
        .optional()
        .default(true)
        .describe("Create the sheet when it cannot be resolved. Defaults to true."),
      titleBlockFamilyName: z
        .string()
        .optional()
        .describe("Title block family name used when the sheet is created."),
      titleBlockTypeName: z
        .string()
        .optional()
        .describe("Title block type name used when the sheet is created."),
      spacing: z
        .number()
        .optional()
        .default(10)
        .describe("Gap between placed elements, mm. Defaults to 10."),
      marginLeft: z
        .number()
        .optional()
        .default(20)
        .describe("Left margin, mm (GOST binding edge). Defaults to 20."),
      marginTop: z.number().optional().default(5).describe("Top margin, mm. Defaults to 5."),
      marginRight: z.number().optional().default(5).describe("Right margin, mm. Defaults to 5."),
      marginBottom: z.number().optional().default(5).describe("Bottom margin, mm. Defaults to 5."),
      titleBlockReserveBottom: z
        .number()
        .optional()
        .default(55)
        .describe(
          "Minimum height reserved for the title block along the sheet bottom, mm. Actual title-block bounds can increase bottom and right reserves. Defaults to 55."
        ),
      order: z
        .enum(["input", "heightDesc", "areaDesc"])
        .optional()
        .default("input")
        .describe(
          "Packing order: input keeps the requested order; heightDesc/areaDesc pack larger elements first for denser layouts."
        ),
      avoidExisting: z
        .boolean()
        .optional()
        .default(true)
        .describe("Treat elements already placed on the sheet as obstacles. Defaults to true."),
      createDependentViewIfNeeded: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "When a view is already on another sheet, duplicate it as a dependent and place the dependent. Defaults to true."
        ),
      autoFitScale: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "When a viewport is larger than the usable area, increase its scale (1:N) until it fits. Defaults to true. Schedules are not scaled."
        ),
      maxViewScale: z
        .number()
        .int()
        .optional()
        .default(500)
        .describe(
          "Maximum scale denominator used by autoFitScale (e.g. 500 means do not go past 1:500). Defaults to 500."
        ),
    },
    async (args) => {
      const params = {
        items: args.items,
        sheetId: args.sheetId ?? 0,
        sheetUniqueId: args.sheetUniqueId ?? "",
        sheetNumber: args.sheetNumber ?? "",
        sheetName: args.sheetName ?? "",
        createSheetIfMissing: args.createSheetIfMissing ?? true,
        titleBlockFamilyName: args.titleBlockFamilyName ?? "",
        titleBlockTypeName: args.titleBlockTypeName ?? "",
        spacing: args.spacing ?? 10,
        marginLeft: args.marginLeft ?? 20,
        marginTop: args.marginTop ?? 5,
        marginRight: args.marginRight ?? 5,
        marginBottom: args.marginBottom ?? 5,
        titleBlockReserveBottom: args.titleBlockReserveBottom ?? 55,
        order: args.order ?? "input",
        avoidExisting: args.avoidExisting ?? true,
        createDependentViewIfNeeded: args.createDependentViewIfNeeded ?? true,
        autoFitScale: args.autoFitScale ?? true,
        maxViewScale: args.maxViewScale ?? 500,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("auto_layout_sheet", params);
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
              text: `Auto layout sheet failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
