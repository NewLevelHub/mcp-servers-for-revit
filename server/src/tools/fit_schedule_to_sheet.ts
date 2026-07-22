import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerFitScheduleToSheetTool(server: McpServer) {
  server.tool(
    "fit_schedule_to_sheet",
    "Make an existing Revit ViewSchedule fit within a target sheet width (REV-68). Applies strategies in order: (1) hide optional columns, (2) narrow all visible columns proportionally, (3) add a Level filter so only one level's rows are shown. Returns finalWidthMm and fits=true when the schedule fits. Use after auto_layout_sheet reports a skip due to width. Always call get_schedule_definition first to see current column widths.",
    {
      // Schedule identifier
      scheduleId: z
        .number()
        .int()
        .optional()
        .describe("Numeric element id of the schedule to fit."),
      scheduleUniqueId: z.string().optional(),
      scheduleName: z.string().optional(),

      // Target width
      maxWidthMm: z
        .number()
        .positive()
        .optional()
        .default(0)
        .describe(
          "Maximum allowed schedule width in mm. 0 = infer from sheetId or use 277 mm (A3 default)."
        ),
      sheetId: z
        .number()
        .int()
        .optional()
        .describe(
          "Sheet element id. Used to infer maxWidthMm automatically when maxWidthMm = 0."
        ),

      // Strategy toggles
      allowHideColumns: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Allow hiding optional columns. Provide optionalColumns to restrict which columns may be hidden."
        ),
      optionalColumns: z
        .array(z.string())
        .optional()
        .default([])
        .describe(
          "Column names that may be hidden. If empty and allowHideColumns=true, any non-calculated visible column is eligible."
        ),
      allowNarrowColumns: z
        .boolean()
        .optional()
        .default(true)
        .describe("Allow shrinking column widths proportionally."),
      minColumnWidthMm: z
        .number()
        .positive()
        .optional()
        .default(15)
        .describe("Minimum column width in mm when narrowing. Default 15 mm."),
      allowLevelFilter: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Allow adding a Level filter so only one level's rows are shown."
        ),
      levelId: z
        .number()
        .int()
        .optional()
        .describe("Level element id for the level filter."),
      levelName: z
        .string()
        .optional()
        .default("")
        .describe(
          "Level name for the level filter (used when levelId is not provided)."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("fit_schedule_to_sheet", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `fit_schedule_to_sheet failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
