import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { RevitClientConnection } from "../utils/SocketClient.js";
import { chunk } from "../utils/titleBlock.js";
import {
  buildNumberingPlan,
  type NumberingUnit,
} from "../utils/roomNumbering.js";

const BATCH_SIZE = 20;
const ROOM_NUMBER_ALIASES = ["Номер", "Number"] as const;

const egressRoomSchema = z.object({
  id: z.number(),
  name: z.string().optional().default(""),
  number: z.string().optional().default(""),
  level: z.string().optional().default(""),
  centroid: z.object({ x: z.number(), y: z.number() }),
});

const parametersItemSchema = z.object({
  elementId: z.number().optional(),
  parameters: z
    .array(
      z.object({
        name: z.string(),
        displayValue: z.string().optional().default(""),
        isReadOnly: z.boolean().optional().default(false),
      })
    )
    .optional()
    .default([]),
});

type RoomParams = z.infer<typeof parametersItemSchema>;

async function fetchRoomParameters(
  revitClient: RevitClientConnection,
  ids: number[]
): Promise<Map<number, RoomParams>> {
  const byId = new Map<number, RoomParams>();
  for (const page of chunk(ids, BATCH_SIZE)) {
    const response = await revitClient.sendCommand("batch_execute", {
      commands: page.map((elementId) => ({
        command: "get_element_parameters",
        params: { elementId },
      })),
    });
    const results = (response as { results?: Array<Record<string, unknown>> })?.results ?? [];
    for (const item of results) {
      if (item.success !== true) continue;
      const parsed = parametersItemSchema.safeParse(item.result);
      if (parsed.success && parsed.data.elementId != null) {
        byId.set(parsed.data.elementId, parsed.data);
      }
    }
  }
  return byId;
}

function paramValue(params: RoomParams | undefined, name: string): string {
  const match = params?.parameters.find(
    (parameter) => parameter.name.toLowerCase() === name.toLowerCase()
  );
  return match?.displayValue?.trim() ?? "";
}

export function registerNumberRoomsTool(server: McpServer) {
  server.tool(
    "number_rooms",
    "Rule-based нумерация помещений и квартир (аналог Autodesk rule-based numbering 2027, локализованный). " +
      "Schemes: 'levelPrefix' (101, 102… / 201, 202… — этаж×множитель + порядковый) or 'continuous' " +
      "(сквозная через этажи). Traversal within a level: 'snake' (змейкой по рядам), 'clockwise' / " +
      "'counterclockwise' (обход вокруг центра). Section prefixes via sectionParameter (e.g. «А-101») " +
      "with a configurable separator. groupByParameter numbers apartments instead of rooms: all rooms " +
      "sharing the value (e.g. ADSK_Номер квартиры) get one new number written back to that parameter. " +
      "PREVIEW BY DEFAULT: apply=false returns the old→new plan without touching the model; set apply=true " +
      "after confirmation. Renumbering is idempotent — deterministic geometric ordering means a re-run " +
      "after adding/removing rooms only rewrites what changed, and a repeat run is a no-op.",
    {
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Level filter, e.g. «1 этаж». Empty = all levels."),
      scheme: z
        .enum(["levelPrefix", "continuous"])
        .optional()
        .default("levelPrefix")
        .describe("'levelPrefix': 101/201 per level; 'continuous': сквозная нумерация через этажи."),
      direction: z
        .enum(["snake", "clockwise", "counterclockwise"])
        .optional()
        .default("snake")
        .describe("Traversal order within a level: змейкой по рядам или по/против часовой вокруг центра."),
      startAt: z.number().int().min(0).optional().default(1),
      levelMultiplier: z
        .number()
        .int()
        .min(10)
        .optional()
        .default(100)
        .describe("levelPrefix base: 100 → 101…; 1000 → 1001… (для этажей с 10+ помещениями)."),
      sectionParameter: z
        .string()
        .optional()
        .default("")
        .describe("Room parameter holding the section/block (e.g. «БС_Секция»). Groups numbering per section."),
      useSectionPrefix: z
        .boolean()
        .optional()
        .default(true)
        .describe("Prepend the section value to the number: «А-101»."),
      separator: z.string().optional().default("-"),
      padWidth: z
        .number()
        .int()
        .min(0)
        .max(4)
        .optional()
        .default(0)
        .describe("Zero-pad the sequence in 'continuous' scheme: 3 → «001»."),
      groupByParameter: z
        .string()
        .optional()
        .default("")
        .describe(
          "Number apartments instead of rooms: rooms sharing this parameter value (e.g. «ADSK_Номер квартиры») form one unit; the new number is written back into this parameter for every room of the unit."
        ),
      rowToleranceMm: z
        .number()
        .positive()
        .optional()
        .default(3000)
        .describe("Snake rows: rooms within this Y distance belong to one row."),
      levelIndexOverrides: z
        .record(z.number().int())
        .optional()
        .describe("Explicit level index map for level names without digits, e.g. {\"Цоколь\": 0}."),
      apply: z
        .boolean()
        .optional()
        .default(false)
        .describe("false (default) = preview only; true = write the numbers after user confirmation."),
      previewLimit: z.number().int().min(1).max(500).optional().default(60),
    },
    async (args) => {
      try {
        const result = await withRevitConnection(async (revitClient) => {
          const rawGraph = await revitClient.sendCommand("export_egress_graph", {
            levelName: args.levelName ?? "",
          });
          const graph = z
            .object({
              success: z.boolean(),
              message: z.string().optional().default(""),
              rooms: z.array(egressRoomSchema).optional().default([]),
            })
            .parse(rawGraph);
          if (!graph.success) throw new Error(graph.message || "export_egress_graph failed.");
          if (graph.rooms.length === 0)
            throw new Error("Помещения не найдены (проверьте levelName и размещение помещений).");

          const warnings: string[] = [];
          const needsParams =
            Boolean(args.sectionParameter) || Boolean(args.groupByParameter);
          const paramsByRoom = needsParams
            ? await fetchRoomParameters(revitClient, graph.rooms.map((room) => room.id))
            : new Map<number, RoomParams>();

          // Build numbering units: rooms, or apartments grouped by parameter value.
          let units: NumberingUnit[];
          if (args.groupByParameter) {
            const groups = new Map<string, typeof graph.rooms>();
            let skipped = 0;
            for (const room of graph.rooms) {
              const value = paramValue(paramsByRoom.get(room.id), args.groupByParameter);
              if (!value) {
                skipped++;
                continue;
              }
              const list = groups.get(value) ?? [];
              list.push(room);
              groups.set(value, list);
            }
            if (skipped > 0) {
              warnings.push(
                `${skipped} помещений без значения «${args.groupByParameter}» пропущены (МОП и т.п.).`
              );
            }
            if (groups.size === 0) {
              throw new Error(
                `Ни одно помещение не имеет значения параметра «${args.groupByParameter}» — группировать нечего.`
              );
            }
            units = [...groups.entries()].map(([value, rooms]) => ({
              ids: rooms.map((room) => room.id),
              label: `Квартира ${value} (${rooms.length} пом.)`,
              level: rooms[0].level,
              x: rooms.reduce((sum, room) => sum + room.centroid.x, 0) / rooms.length,
              y: rooms.reduce((sum, room) => sum + room.centroid.y, 0) / rooms.length,
              current: value,
              section: args.sectionParameter
                ? paramValue(paramsByRoom.get(rooms[0].id), args.sectionParameter) || undefined
                : undefined,
            }));
          } else {
            units = graph.rooms.map((room) => ({
              ids: [room.id],
              label: `${room.name}${room.number ? ` (${room.number})` : ""}`,
              level: room.level,
              x: room.centroid.x,
              y: room.centroid.y,
              current: room.number,
              section: args.sectionParameter
                ? paramValue(paramsByRoom.get(room.id), args.sectionParameter) || undefined
                : undefined,
            }));
          }

          if (args.sectionParameter) {
            const missing = units.filter((unit) => !unit.section).length;
            if (missing > 0) {
              warnings.push(
                `${missing} единиц без значения секции «${args.sectionParameter}» — нумеруются без префикса.`
              );
            }
          }

          const plan = buildNumberingPlan(units, {
            scheme: args.scheme,
            direction: args.direction,
            startAt: args.startAt,
            levelMultiplier: args.levelMultiplier,
            useSectionPrefix: args.useSectionPrefix,
            separator: args.separator,
            padWidth: args.padWidth,
            rowToleranceMm: args.rowToleranceMm,
            levelIndexOverrides: args.levelIndexOverrides,
          });
          warnings.push(...plan.warnings);

          if (!args.apply) {
            return { plan, warnings, applied: false, written: 0, failures: [] as string[] };
          }

          // Write: room Number, or the grouping parameter for apartments.
          const targetParameter = args.groupByParameter || null;
          const writeOps = plan.assignments.flatMap((assignment) =>
            assignment.ids.map((elementId) => ({
              elementId,
              value: assignment.to,
              label: assignment.label,
            }))
          );

          let written = 0;
          const failures: string[] = [];
          for (const page of chunk(writeOps, BATCH_SIZE)) {
            const response = await revitClient.sendCommand("batch_execute", {
              commands: page.map((op) => ({
                command: "set_element_parameter",
                params: {
                  elementId: op.elementId,
                  // set_element_parameter matches by localized name; try RU then EN.
                  parameterName: targetParameter ?? ROOM_NUMBER_ALIASES[0],
                  value: op.value,
                },
              })),
            });
            const results =
              (response as { results?: Array<Record<string, unknown>> })?.results ?? [];
            for (let i = 0; i < results.length; i++) {
              const item = results[i];
              const ok =
                item.success === true &&
                (item.result as Record<string, unknown> | undefined)?.success !== false;
              if (ok) {
                written++;
                continue;
              }
              // RU name missed → retry once with the EN alias for room numbers.
              if (!targetParameter) {
                const retry = await revitClient.sendCommand("set_element_parameter", {
                  elementId: page[i].elementId,
                  parameterName: ROOM_NUMBER_ALIASES[1],
                  value: page[i].value,
                });
                if ((retry as { success?: boolean })?.success) {
                  written++;
                  continue;
                }
              }
              const error =
                ((item.result as Record<string, unknown> | undefined)?.message as string) ??
                ((item.error as Record<string, unknown> | undefined)?.message as string) ??
                "unknown error";
              failures.push(`${page[i].label}: ${error}`);
            }
          }

          return { plan, warnings, applied: true, written, failures };
        });

        const { plan } = result;
        const previewRows = plan.assignments.slice(0, args.previewLimit ?? 60);
        const lines: string[] = [
          result.applied
            ? `## Нумерация записана: изменено ${result.written} значений`
            : "## Предпросмотр нумерации (модель НЕ изменена)",
          "",
          `- Единиц всего: **${plan.totalUnits}**, будет изменено: **${plan.assignments.length}**, уже верно (идемпотентно): **${plan.unchangedCount}**`,
        ];
        if (result.failures.length > 0) {
          lines.push(`- Ошибок записи: **${result.failures.length}**`);
        }
        lines.push("", "| Помещение / квартира | Этаж | Было | Станет |", "| --- | --- | --- | --- |");
        for (const row of previewRows) {
          lines.push(`| ${row.label} | ${row.level} | ${row.from || "—"} | **${row.to}** |`);
        }
        if (plan.assignments.length > previewRows.length) {
          lines.push(`| … ещё ${plan.assignments.length - previewRows.length} | | | |`);
        }
        if (!result.applied && plan.assignments.length > 0) {
          lines.push("", "> Проверьте план и повторите вызов с `apply: true` для записи.");
        }

        return {
          content: [
            { type: "text" as const, text: lines.join("\n") },
            {
              type: "text" as const,
              text: JSON.stringify(
                {
                  applied: result.applied,
                  totalUnits: plan.totalUnits,
                  changed: plan.assignments.length,
                  unchanged: plan.unchangedCount,
                  written: result.written,
                  failures: result.failures,
                  warnings: result.warnings,
                  assignments: plan.assignments,
                },
                null,
                2
              ),
            },
          ],
          isError: result.failures.length > 0 && result.written === 0 && result.applied,
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `number_rooms failed: ${
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
