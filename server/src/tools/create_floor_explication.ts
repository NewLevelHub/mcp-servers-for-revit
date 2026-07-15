import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const explicationSchema = z.object({
  name: z
    .string()
    .optional()
    .default("О_АР_Экспликация полов")
    .describe("Base name when not split. Split mode uses discovered titles (e.g. Экспликация полов 1-го этажа)."),
  useRecipe: z
    .boolean()
    .optional()
    .default(true)
    .describe(
      "Default true: build from seeded «Короткий блок» recipe (columns + Totals area + (полы) filter)."
    ),
  splitByLevelGroups: z
    .boolean()
    .optional()
    .default(true)
    .describe(
      "Default true: discover levels with (полы)* finishes; merge consecutive storeys that share the same floor type(s) and XY footprint into one title (e.g. 3-6-го этажа)."
    ),
  templateId: z
    .string()
    .optional()
    .default("")
    .describe("Optional: duplicate an existing Floors schedule instead of the recipe."),
  templateName: z
    .string()
    .optional()
    .default("")
    .describe("Optional duplicate by schedule name."),
  discoverTemplate: z
    .boolean()
    .optional()
    .default(false)
    .describe("Only when useRecipe=false: auto-find экспликация schedule."),
  placeOnSheet: z
    .boolean()
    .optional()
    .default(true)
    .describe("Create sheet(s) with ADSK_ОсновнаяНадпись Форма 3 and place schedule(s)."),
  sheetNumber: z
    .string()
    .optional()
    .default("")
    .describe("Only for single-schedule mode. Split mode auto-numbers ЭП-01, ЭП-02, …"),
  sheetName: z
    .string()
    .optional()
    .default("Экспликация полов")
    .describe("Only for single-schedule mode. Split mode uses RD titles per group."),
  titleBlockFamilyName: z
    .string()
    .optional()
    .default("ADSK_ОсновнаяНадпись")
    .describe("Title block family. Default ADSK_ОсновнаяНадпись."),
  titleBlockTypeName: z
    .string()
    .optional()
    .default("Форма 3")
    .describe("Title block type. Default Форма 3."),
  positionX: z
    .number()
    .optional()
    .default(20)
    .describe("Schedule X on sheet in mm from lower-left."),
  positionY: z
    .number()
    .optional()
    .default(200)
    .describe("Schedule Y on sheet in mm from lower-left."),
});

export function registerCreateFloorExplicationTool(server: McpServer) {
  server.tool(
    "create_floor_explication",
    "Create floor экспликация using seeded «Короткий блок» columns. By default discovers levels that have (полы)* finishes, merges typified consecutive storeys (same type + same plan location) into titles like «2-го этажа» or «3-6-го этажа», and places ALL schedules on ONE sheet. Set splitByLevelGroups=false for one combined schedule. For data-only export use create_floor_schedule.",
    {
      explication: explicationSchema
        .optional()
        .describe("Creation options. Omit for RD-style multi-sheet create."),
    },
    async (args) => {
      const params = {
        explication: args.explication ?? {},
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_floor_explication", params);
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
              text: `Create floor экспликация failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
