import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  paginateScheduleExport,
  scheduleExportPaginationSchema,
  ScheduleExportPaginationArgs,
} from "../utils/ScheduleExportPagination.js";

export function registerCreateCurtainWallScheduleTool(server: McpServer) {
  server.tool(
    "create_curtain_wall_schedule",
    "Export structured curtain wall (витражи) schedule data. Counts curtain wall SYSTEMS — Wall elements with a Curtain wall type, e.g. types '(витражи)1200х2900h' — never glazing panels or mullions, and never Windows category. Returns groups by type with elementIds (truncated per maxElementIdsPerGroup), mark (BB/BH), size (length x height mm), level, and count; the per-instance list is omitted by default — page through it with includeInstances/instancesOffset/instancesLimit. Use validate_schedule with category 'CurtainWalls' to compare against the 'Спецификация витражей' schedule.",
    {
      typeNameFilter: z
        .string()
        .optional()
        .describe(
          "Optional wall type name substring to narrow the export, e.g. '(витражи)'. Empty includes every curtain wall."
        ),
      ...scheduleExportPaginationSchema,
    },
    async (args: ScheduleExportPaginationArgs & { typeNameFilter?: string }) => {
      const params = {
        typeNameFilter: args.typeNameFilter ?? "",
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_curtain_wall_schedule", params);
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
              text: `Create curtain wall schedule failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
