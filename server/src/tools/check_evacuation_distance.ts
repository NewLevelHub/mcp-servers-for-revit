import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  classifyRoutes,
  MGN_DEAD_END_LIMIT_M,
  MGN_DEAD_END_SOURCE,
  traceEvacuationRoutes,
  type ClassifiedRoute,
  type EgressDoor,
  type EgressRoom,
} from "../normatives/normAudit/evacuationDistance.js";
import type { NormAuditSource } from "../normatives/normAudit/types.js";

const pointSchema = z.object({ x: z.number(), y: z.number() });

const egressGraphSchema = z.object({
  success: z.boolean(),
  message: z.string().optional().default(""),
  levelName: z.string().optional().default(""),
  rooms: z
    .array(
      z.object({
        id: z.number(),
        uniqueId: z.string().optional(),
        name: z.string().optional().default(""),
        number: z.string().optional().default(""),
        level: z.string().optional().default(""),
        centroid: pointSchema,
        boundary: z.array(pointSchema),
      })
    )
    .optional()
    .default([]),
  doors: z
    .array(
      z.object({
        id: z.number(),
        uniqueId: z.string().optional(),
        name: z.string().optional().default(""),
        level: z.string().optional().default(""),
        x: z.number(),
        y: z.number(),
        fromRoomId: z.number().nullable().optional(),
        toRoomId: z.number().nullable().optional(),
        widthMm: z.number().nullable().optional(),
        isExteriorWall: z.boolean().optional().default(false),
      })
    )
    .optional()
    .default([]),
  warnings: z.array(z.string()).optional().default([]),
});

function statusBadge(status: ClassifiedRoute["status"]): string {
  switch (status) {
    case "violation":
      return "❌";
    case "nearLimit":
      return "⚠️";
    case "compliant":
      return "✅";
    default:
      return "📏";
  }
}

function formatReport(
  routes: ClassifiedRoute[],
  source: NormAuditSource | undefined,
  limits: { maxDeadEndM?: number; maxThroughM?: number },
  meta: {
    levelName: string;
    exitCount: number;
    unreachable: Array<{ roomName: string }>;
    warnings: string[];
  }
): string {
  const lines: string[] = [
    "## Длина путей эвакуации до выхода",
    "",
    `- Уровень: **${meta.levelName || "текущий / все"}**, эвакуационных выходов найдено: **${meta.exitCount}**`,
    `- Лимиты: тупиковый ≤ **${limits.maxDeadEndM ?? "—"} м**, сквозной ≤ **${limits.maxThroughM ?? "—"} м**`,
  ];

  if (source) {
    lines.push(`- Норма: ${source.document}${source.clause ? `, ${source.clause}` : ""}`);
    lines.push(`  - «${source.quote}»`);
  }

  const violations = routes.filter((r) => r.status === "violation" || r.status === "nearLimit");
  lines.push(
    "",
    `Маршрутов прослежено: **${routes.length}**, нарушений: **${routes.filter((r) => r.status === "violation").length}**, пограничных: **${routes.filter((r) => r.status === "nearLimit").length}**`,
    ""
  );

  const top = violations.length > 0 ? violations : routes.slice(0, 15);
  lines.push(violations.length > 0 ? "### Нарушения и пограничные" : "### Самые длинные маршруты");
  for (const route of top) {
    const kind = route.corridorKind === "deadEnd" ? "тупиковый" : "сквозной";
    lines.push(
      `- ${statusBadge(route.status)} **${route.roomName}${route.roomNumber ? ` (${route.roomNumber})` : ""}** — ` +
        `${route.lengthM} м (${kind}, выходов достижимо: ${route.reachableExits})` +
        (route.limitM != null ? `, предел ${route.limitM} м` : "") +
        (route.deviationM ? `, превышение ${route.deviationM} м` : "") +
        ` · room id ${route.roomId}`
    );
  }

  if (meta.unreachable.length > 0) {
    lines.push(
      "",
      `⚠️ Помещений без пути к выходу: ${meta.unreachable.length} — ${meta.unreachable
        .slice(0, 8)
        .map((r) => r.roomName)
        .join(", ")}`
    );
  }

  if (meta.warnings.length > 0) {
    lines.push("", "### Предупреждения");
    for (const warning of meta.warnings) lines.push(`- ${warning}`);
  }

  lines.push(
    "",
    "> Трассировка по графу проходимости (двери → помещения → двери), длина — по " +
      "ломаной внутри контуров помещений, не по прямой. Мебель и лестничные марши не учитываются."
  );

  return lines.join("\n");
}

export function registerCheckEvacuationDistanceTool(server: McpServer) {
  server.tool(
    "check_evacuation_distance",
    "Расчёт длины путей эвакуации до выхода. Traces walkable escape routes on the egress graph exported " +
      "from Revit (export_egress_graph): rooms → doors → corridors → exit, path length along geometry inside " +
      "room boundary polygons — NOT straight-line distance. Exits = doors in exterior walls and doors into " +
      "stairwell rooms (лестничная клетка; extend via exitRoomTokens). Corridor kind is inferred per start " +
      "door: one reachable exit → тупиковый (dead-end limit), two or more → сквозной (through limit). " +
      "Limits: pass maxDeadEndM/maxThroughM with source (e.g. 25/40 м по СН РК 2.02-01) or preset='mgn' " +
      "(тупиковый ≤ 15 м, СП РК 3.06-101-2012* п. 4.2.4, verbatim quote included). Without limits routes are " +
      "measured and reported without verdicts. mode='visualize' draws routes as red detail lines on the " +
      "active plan (create_detail_lines + SetColor) and fills violating rooms red (create_filled_regions).",
    {
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Level filter, e.g. «1 этаж». Empty = whole model (per-level graphs are cleaner)."),
      preset: z
        .enum(["none", "mgn"])
        .optional()
        .default("none")
        .describe("'mgn' applies тупиковый ≤ 15 м per СП РК 3.06-101-2012* п. 4.2.4 with the verbatim quote."),
      maxDeadEndM: z
        .number()
        .positive()
        .optional()
        .describe("Limit for dead-end corridors, м (e.g. 25 for Ф1.3 по СН РК 2.02-01). Overrides preset."),
      maxThroughM: z
        .number()
        .positive()
        .optional()
        .describe("Limit between stairwells / through corridors, м (e.g. 40)."),
      source: z
        .object({
          document: z.string(),
          clause: z.string().optional().default(""),
          quote: z.string().optional().default(""),
        })
        .optional()
        .describe("Norm citation for manually passed limits — included in the report."),
      exitRoomTokens: z
        .array(z.string())
        .optional()
        .describe("Extra room-name tokens treated as exits, e.g. ['вестибюль', 'тамбур выход']."),
      startRoomTokens: z
        .array(z.string())
        .optional()
        .describe("Trace only rooms whose name contains these tokens (e.g. ['квартира']). Default: all non-corridor rooms."),
      nearLimitToleranceM: z.number().nonnegative().optional().default(1),
      mode: z
        .enum(["report", "visualize"])
        .optional()
        .default("report")
        .describe("'visualize' also draws route polylines (red detail lines) and fills violating rooms on the active plan."),
    },
    async (args) => {
      try {
        const limits = {
          maxDeadEndM:
            args.maxDeadEndM ?? (args.preset === "mgn" ? MGN_DEAD_END_LIMIT_M : undefined),
          maxThroughM: args.maxThroughM,
        };
        const source: NormAuditSource | undefined = args.source
          ? { document: args.source.document, clause: args.source.clause ?? "", quote: args.source.quote ?? "" }
          : args.preset === "mgn"
            ? MGN_DEAD_END_SOURCE
            : undefined;

        const warnings: string[] = [];
        if (limits.maxDeadEndM == null && limits.maxThroughM == null) {
          warnings.push(
            "Лимиты не заданы (preset='none') — маршруты измерены без вердиктов. " +
              "Передайте maxDeadEndM/maxThroughM с source или preset='mgn'."
          );
        }

        const result = await withRevitConnection(async (revitClient) => {
          const rawGraph = await revitClient.sendCommand("export_egress_graph", {
            levelName: args.levelName ?? "",
          });
          const graph = egressGraphSchema.parse(rawGraph);
          if (!graph.success) {
            throw new Error(graph.message || "export_egress_graph failed.");
          }
          warnings.push(...graph.warnings);

          const rooms: EgressRoom[] = graph.rooms.map((room) => ({ ...room }));
          const doors: EgressDoor[] = graph.doors.map((door) => ({ ...door }));

          const traced = traceEvacuationRoutes(rooms, doors, {
            exitRoomTokens: args.exitRoomTokens,
            startRoomTokens: args.startRoomTokens,
          });
          warnings.push(...traced.warnings);

          const routes = classifyRoutes(traced.routes, { ...limits, source }, args.nearLimitToleranceM ?? 1);

          let drawnSegments = 0;
          let paintedRooms = 0;
          if (args.mode === "visualize" && routes.length > 0) {
            const toDraw = routes.filter(
              (route) => route.status === "violation" || route.status === "nearLimit"
            );
            const selection = toDraw.length > 0 ? toDraw : routes.slice(0, 10);
            const linesResponse = await revitClient.sendCommand("create_detail_lines", {
              polylines: selection.map((route) => ({ points: route.polyline })),
            });
            const lineIds =
              (linesResponse as { detailLineIds?: number[] })?.detailLineIds ?? [];
            drawnSegments = lineIds.length;
            if (lineIds.length > 0) {
              await revitClient.sendCommand("operate_element", {
                data: { elementIds: lineIds, action: "SetColor", colorValue: [255, 0, 0] },
              });
            }

            const violatingRoomIds = routes
              .filter((route) => route.status === "violation")
              .map((route) => route.roomId);
            if (violatingRoomIds.length > 0) {
              const regions = await revitClient.sendCommand("create_filled_regions", {
                roomIds: violatingRoomIds,
                colorPreset: "red",
                clearPrevious: true,
                commentTag: "MCP-EVAC",
              });
              paintedRooms =
                Number((regions as { createdCount?: number })?.createdCount) || 0;
            }
          }

          return {
            levelName: graph.levelName,
            routes,
            exitDoorIds: traced.exitDoorIds,
            unreachableRooms: traced.unreachableRooms,
            drawnSegments,
            paintedRooms,
          };
        });

        const report = formatReport(result.routes, source, limits, {
          levelName: result.levelName || args.levelName || "",
          exitCount: result.exitDoorIds.length,
          unreachable: result.unreachableRooms,
          warnings,
        });

        const jsonPayload = {
          success: true,
          levelName: result.levelName,
          limits: { ...limits, source: source ?? null },
          exitDoorIds: result.exitDoorIds,
          totalRoutes: result.routes.length,
          violations: result.routes.filter((r) => r.status === "violation").length,
          nearLimit: result.routes.filter((r) => r.status === "nearLimit").length,
          unreachableRooms: result.unreachableRooms,
          drawnSegments: result.drawnSegments,
          paintedRooms: result.paintedRooms,
          routes: result.routes.map((route) => ({
            roomId: route.roomId,
            roomName: route.roomName,
            roomNumber: route.roomNumber,
            level: route.level,
            lengthM: route.lengthM,
            limitM: route.limitM ?? null,
            status: route.status,
            corridorKind: route.corridorKind,
            reachableExits: route.reachableExits,
            startDoorId: route.startDoorId,
            exitDoorId: route.exitDoorId,
            hasDetours: route.hasDetours,
          })),
          warnings,
        };

        return {
          content: [
            { type: "text" as const, text: report },
            { type: "text" as const, text: JSON.stringify(jsonPayload, null, 2) },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `check_evacuation_distance failed: ${
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
