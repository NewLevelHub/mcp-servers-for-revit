import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetScheduleDefinitionTool(server: McpServer) {
  server.tool(
    "get_schedule_definition",
    "Read the full structure of an existing Revit ViewSchedule by id, uniqueId, or name. Returns fields (parameter, heading, width in mm, alignment, type), filters, sorts, groups, and display flags ShowTitle/ShowHeaders/ShowGridLines. Use to replicate template schedules such as ADSK TEP or finish schedules.",
    {
      scheduleId: z
        .number()
        .int()
        .optional()
        .describe("Element id of the schedule view."),
      scheduleUniqueId: z
        .string()
        .optional()
        .describe("UniqueId of the schedule view."),
      scheduleName: z
        .string()
        .optional()
        .describe(
          "Schedule view name, e.g. 'О_АР_Квартиры_ТЭП' or 'ADSK_О_С_С'. Works for templates and project schedules."
        ),
    },
    async (args) => {
      if (
        args.scheduleId == null &&
        !args.scheduleUniqueId &&
        !args.scheduleName
      ) {
        return {
          content: [
            {
              type: "text",
              text: "Get schedule definition failed: provide scheduleId, scheduleUniqueId, or scheduleName.",
            },
          ],
        };
      }

      const params = {
        scheduleId: args.scheduleId ?? null,
        scheduleUniqueId: args.scheduleUniqueId ?? null,
        scheduleName: args.scheduleName ?? null,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(
            "get_schedule_definition",
            params
          );
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
              text: `Get schedule definition failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
