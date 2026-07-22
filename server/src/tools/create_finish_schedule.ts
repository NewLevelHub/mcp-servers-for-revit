import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const finishScheduleSchema = z.object({
  name: z
    .string()
    .optional()
    .default("Room Finish Schedule")
    .describe("Base name for the created schedule(s)"),
  templateId: z
    .string()
    .optional()
    .default("")
    .describe(
      "UniqueId or ElementId of a single room finish schedule template. When provided without chain template names, the pre-chain single-schedule behavior is used."
    ),
  type: z
    .enum(["Regular", "KeySchedule", "MaterialTakeoff"])
    .optional()
    .default("Regular"),
  reproduceTemplateChain: z
    .boolean()
    .optional()
    .default(true)
    .describe(
      "Reproduce the full ADSK_RU finish schedule chain: key styles schedule → data-filling schedule → output finish list(s). Defaults to true."
    ),
  keyScheduleTemplateName: z
    .string()
    .optional()
    .default("")
    .describe(
      "Key (styles) schedule template name, e.g. 'В_Отделка-помещения-01_Стили_Ключевая'. Auto-discovered by ADSK_RU naming when omitted."
    ),
  dataScheduleTemplateName: z
    .string()
    .optional()
    .default("")
    .describe(
      "Data-filling schedule template name, e.g. 'В_Отделка-помещения-02_Заполнение данных'. Auto-discovered when omitted."
    ),
  outputScheduleTemplateNames: z
    .array(z.string())
    .optional()
    .default([])
    .describe(
      "Output finish list template names, e.g. ['О_АР_Ведомость отделки помещений_Имя', 'О_АР_Ведомость отделки помещений_Номер']. Auto-discovered when omitted."
    ),
  duplicateKeySchedule: z
    .boolean()
    .optional()
    .default(false)
    .describe(
      "Duplicate the key schedule instead of reusing it. Off by default: a duplicated key schedule defines its own key parameter and loses the link to the data schedule."
    ),
  includeUnplacedRooms: z.boolean().optional().default(false),
  includeNotEnclosedRooms: z.boolean().optional().default(false),
  missingFinishWarningThreshold: z
    .number()
    .optional()
    .default(0.3)
    .describe("Warn when share of rooms without finish data exceeds this ratio (0.3 = 30%)"),
});

export function registerCreateFinishScheduleTool(server: McpServer) {
  server.tool(
    "create_finish_schedule",
    "Create a room finish schedule set in Revit. By default reproduces the ADSK_RU template chain: key styles schedule (В_Отделка-помещения-01_Стили_Ключевая) → data-filling schedule (В_Отделка-помещения-02_Заполнение данных) → output finish list(s) (О_АР_Ведомость отделки помещений_Имя/_Номер). The key schedule is reused to keep its key parameter linked; data and output schedules are duplicated with template formatting. Rows are rooms; columns are finish types. Falls back to a single schedule when no chain templates exist; its default columns resolve by built-in parameter ids, so localized (e.g. Russian) projects work without English parameter names. Fails with an explicit error instead of creating an empty schedule when no fields resolve. Warns when more than 30% of rooms lack finish parameters.",
    {
      schedule: finishScheduleSchema
        .optional()
        .describe("Finish schedule settings"),
      includeUnplacedRooms: z
        .boolean()
        .optional()
        .describe("Include unplaced rooms in finish data validation"),
      includeNotEnclosedRooms: z
        .boolean()
        .optional()
        .describe("Include not enclosed rooms in finish data validation"),
    },
    async (args) => {
      const schedule = {
        ...finishScheduleSchema.parse({}),
        ...args.schedule,
        includeUnplacedRooms:
          args.schedule?.includeUnplacedRooms ?? args.includeUnplacedRooms ?? false,
        includeNotEnclosedRooms:
          args.schedule?.includeNotEnclosedRooms ?? args.includeNotEnclosedRooms ?? false,
      };

      const params = { schedule };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_finish_schedule", params);
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
              text: `Create finish schedule failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
