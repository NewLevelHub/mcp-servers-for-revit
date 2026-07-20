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
    },
    async (args: ScheduleExportAndViewArgs) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(commandName, {
            createViewSchedule: args.createViewSchedule ?? false,
            scheduleName: args.scheduleName ?? null,
            replaceExisting: args.replaceExisting ?? false,
          });
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(paginateScheduleExport(response, args), null, 2),
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
    "Export structured door schedule data for door blocks only (excludes slopes/reveals and similar accessories by family/type name). Returns rows grouped by family type with mark, type, size, level, elementIds (truncated per maxElementIdsPerGroup), and count. The per-instance list is omitted by default — page through it with includeInstances/instancesOffset/instancesLimit. Foundation for create_schedule and validate_schedule workflows."
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
