import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

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
    {},
    async () => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(commandName, {});
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
    "Export structured door schedule data for door blocks only (excludes slopes/reveals and similar accessories by family/type name). Returns all instances (including those without mark) plus rows grouped by family type with mark, type, size, level, elementIds, and count. Foundation for create_schedule and validate_schedule workflows."
  );
}

export function registerCreateWindowScheduleTool(server: McpServer) {
  registerScheduleExportTool(
    server,
    "create_window_schedule",
    "create_window_schedule",
    "window",
    "Export structured window schedule data for window blocks only (excludes slopes/sills and similar accessories by family/type name). Returns all instances (including those without mark) plus rows grouped by family type with mark, type, size, level, elementIds, and count. Foundation for create_schedule and validate_schedule workflows."
  );
}

export function registerCreateFloorScheduleTool(server: McpServer) {
  registerScheduleExportTool(
    server,
    "create_floor_schedule",
    "create_floor_schedule",
    "floor",
    "Export structured floor schedule data from the current Revit project. Returns all instances (including those without mark) plus rows grouped by family type with mark, type, size, level, elementIds, and count. Foundation for create_schedule and validate_schedule workflows."
  );
}
