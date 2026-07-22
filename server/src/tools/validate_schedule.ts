import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const categorySchema = z.enum(["Doors", "Windows", "Floors", "CurtainWalls"]);

export function registerValidateScheduleTool(server: McpServer) {
  server.tool(
    "validate_schedule",
    "Compare a Revit schedule against model elements for Doors, Windows, Floors, or CurtainWalls. For Doors/Windows, model side excludes slopes/reveals (same filter as create_door_schedule / create_window_schedule) and returns element id diffs. For Floors, compares finish-floor areas (m²) by type against an экспликация/area schedule — prefers schedules named экспликация/ведомость полов and skips key/style schedules; returns modelAreaM2, scheduleAreaM2, areaDiffM2, typeAreas. CurtainWalls counts curtain wall SYSTEMS (Wall with Curtain type), not panels/mullions, and by default matches 'Спецификация витражей'. Optionally filter by schedule name and/or level.",
    {
      category: categorySchema.describe(
        "Element category to validate: Doors, Windows, Floors, or CurtainWalls (витражи — curtain wall systems)."
      ),
      scheduleName: z
        .string()
        .optional()
        .describe(
          "Optional schedule view name. If omitted: Doors/Windows use first category schedule; Floors prefer экспликация/ведомость полов over key/style schedules; CurtainWalls prefer спецификация витражей."
        ),
      levelName: z
        .string()
        .optional()
        .describe("Optional level name to limit comparison to elements on that level."),
      levelId: z
        .number()
        .int()
        .optional()
        .describe("Optional level element id. Alternative to levelName."),
    },
    async (args) => {
      const params = {
        category: args.category,
        scheduleName: args.scheduleName ?? null,
        levelName: args.levelName ?? null,
        levelId: args.levelId ?? null,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("validate_schedule", params);
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
              text: `Validate schedule failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
