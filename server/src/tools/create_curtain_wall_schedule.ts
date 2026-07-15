import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCreateCurtainWallScheduleTool(server: McpServer) {
  server.tool(
    "create_curtain_wall_schedule",
    "Export structured curtain wall (витражи) schedule data. Counts curtain wall SYSTEMS — Wall elements with a Curtain wall type, e.g. types '(витражи)1200х2900h' — never glazing panels or mullions, and never Windows category. Returns instances and groups by type with elementIds, mark (BB/BH), size (length x height mm), level, and count. Use validate_schedule with category 'CurtainWalls' to compare against the 'Спецификация витражей' schedule.",
    {
      typeNameFilter: z
        .string()
        .optional()
        .describe(
          "Optional wall type name substring to narrow the export, e.g. '(витражи)'. Empty includes every curtain wall."
        ),
    },
    async (args) => {
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
              text: JSON.stringify(response, null, 2),
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
