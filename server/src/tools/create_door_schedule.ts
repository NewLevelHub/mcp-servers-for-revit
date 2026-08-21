import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  paginateScheduleExport,
  scheduleExportPaginationSchema,
  ScheduleExportPaginationArgs,
} from "../utils/ScheduleExportPagination.js";

interface ScheduleExportAndViewArgs extends ScheduleExportPaginationArgs {
  createViewSchedule?: boolean;
  scheduleName?: string;
  replaceExisting?: boolean;
  templateScheduleName?: string;
  templateId?: string;
}

function registerScheduleExportTool(
  server: McpServer,
  toolName: string,
  commandName: string,
  elementLabel: string,
  description: string
) {
  server.tool(
    toolName,
    description,
    {
      ...scheduleExportPaginationSchema,
      createViewSchedule: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Also create a real Revit ViewSchedule for this category, so validate_schedule can compare the schedule view against the model."
        ),
      scheduleName: z
        .string()
        .optional()
        .describe(
          "Optional Revit ViewSchedule name. Defaults to 'Спецификация дверей' or 'Спецификация окон'."
        ),
      replaceExisting: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "When createViewSchedule=true, delete and recreate an existing schedule with the same name."
        ),
      templateScheduleName: z
        .string()
        .optional()
        .describe(
          "RD door schedule template name to duplicate (e.g. 'О_АР_Спецификация элементов заполнения дверных проемов поэтжная'). When omitted, auto-finds the project RD template."
        ),
      templateId: z
        .string()
        .optional()
        .describe(
          "ElementId or UniqueId of an existing door ViewSchedule to duplicate as template."
        ),
    },
    async (args: ScheduleExportAndViewArgs) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(commandName, {
            createViewSchedule: args.createViewSchedule ?? false,
            scheduleName: args.scheduleName ?? null,
            replaceExisting: args.replaceExisting ?? false,
            templateScheduleName: args.templateScheduleName ?? null,
            templateId: args.templateId ?? null,
          });
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(paginateScheduleExport(response, args)),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Export ${elementLabel} schedule failed: ${
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

export function registerCreateDoorScheduleTool(server: McpServer) {
  registerScheduleExportTool(
    server,
    "create_door_schedule",
    "create_door_schedule",
    "door",
    "Export structured door schedule data for door blocks only (excludes slopes/reveals and similar accessories by family/type name). Returns rows grouped by family type with mark, type, size, level, elementIds (truncated per maxElementIdsPerGroup), and count. When createViewSchedule=true, duplicates the project RD door schedule template (поэтажная матрица: Поз., Обозначение, Наименование, колонки этажей, Итого) instead of a bare 4-column list. Falls back to built-in RD column layout if no template is found."
  );
}

export function registerCreateWindowScheduleTool(server: McpServer) {
  registerScheduleExportTool(
    server,
    "create_window_schedule",
    "create_window_schedule",
    "window",
    "Export structured window schedule data for window blocks only (excludes slopes/sills and similar accessories by family/type name). Returns rows grouped by family type with mark, type, size, level, elementIds (truncated per maxElementIdsPerGroup), and count. The per-instance list is omitted by default — page through it with includeInstances/instancesOffset/instancesLimit. Foundation for create_schedule and validate_schedule workflows."
  );
}

export function registerCreateFloorScheduleTool(server: McpServer) {
  registerScheduleExportTool(
    server,
    "create_floor_schedule",
    "create_floor_schedule",
    "floor",
    "Export floor finish экспликация from the model: finish floors only (e.g. types (полы)*), excluding structural slabs, ceiling insulation, and facade floor-like types. Returns groups by type/level with areaM2 (m²), optional compound layers, totalAreaM2, and count; the per-instance list is omitted by default — page through it with includeInstances/instancesOffset/instancesLimit. Use this for floor area reports — not for counting all OST_Floors."
  );
}
