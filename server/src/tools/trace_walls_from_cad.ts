import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  traceWallAxesFromCad,
  verifyAxesAgainstCad,
  type CadSegment,
  type BboxMm,
} from "../cad/cadWallTracing.js";

const bboxSchema = z.object({
  minX: z.number(),
  maxX: z.number(),
  minY: z.number(),
  maxY: z.number(),
});

type CadGeometryResponse = {
  ok?: boolean;
  success?: boolean;
  summary?: string;
  message?: string;
  count?: number;
  items?: CadSegment[];
  bboxMm?: BboxMm;
  cadLinkName?: string;
  viewId?: number;
  viewName?: string;
};

type ViewInfoResponse = {
  elevation?: number;
  levelElevation?: number;
  LevelElevation?: number;
  name?: string;
  Name?: string;
};

type CreateWallResponse = {
  success?: boolean;
  Success?: boolean;
  data?: number[];
  Data?: number[];
  elementIds?: number[];
  warnings?: string[];
  errors?: string[];
  message?: string;
};

function readElevation(view: ViewInfoResponse): number {
  return (
    view.elevation ??
    view.levelElevation ??
    view.LevelElevation ??
    0
  );
}

function isSuccess(resp: { success?: boolean; Success?: boolean; ok?: boolean }): boolean {
  return resp.success === true || resp.Success === true || resp.ok === true;
}

function extractElementIds(resp: CreateWallResponse): number[] {
  return resp.data ?? resp.Data ?? resp.elementIds ?? [];
}

export function registerTraceWallsFromCadTool(server: McpServer) {
  server.tool(
    "trace_walls_from_cad",
    "Trace walls from DWG/CAD on the active floor plan (REV-140). " +
      "Reads segments via get_cad_link_geometry, merges double lines to centerlines, " +
      "merges collinear gaps, creates walls with create_line_based_element. " +
      "Requires wallTypeId from get_available_family_types. " +
      "Returns verify stats (maxDeviationMm). Use dryRun to preview axes without creating.",
    {
      wallTypeId: z
        .number()
        .int()
        .positive()
        .describe("Required. Wall type ElementId from get_available_family_types (OST_Walls)."),
      cadLinkName: z
        .string()
        .optional()
        .describe("Optional CAD link name filter (substring)."),
      layerFilter: z
        .union([z.string(), z.array(z.string())])
        .optional()
        .describe("DWG layer filter, e.g. 'wall' or ['WALL','Стены']."),
      bboxMm: bboxSchema
        .optional()
        .describe(
          "Clip region in mm {minX,maxX,minY,maxY}. Use to exclude distant DWG fragments."
        ),
      toleranceMm: z
        .number()
        .optional()
        .default(50)
        .describe("Merge gap / verify tolerance in mm (default 50)."),
      mergeGapMm: z
        .number()
        .optional()
        .describe("Max gap to merge collinear axes (default = toleranceMm)."),
      minPairGapMm: z
        .number()
        .optional()
        .default(50)
        .describe("Min distance between parallel DWG lines to pair (default 50)."),
      maxPairGapMm: z
        .number()
        .optional()
        .default(500)
        .describe("Max distance between parallel DWG lines to pair (default 500)."),
      minWallLengthMm: z
        .number()
        .optional()
        .default(300)
        .describe("Skip wall axes shorter than this (default 300)."),
      minLengthMm: z
        .number()
        .optional()
        .default(300)
        .describe("Min CAD segment length passed to get_cad_link_geometry (default 300)."),
      heightMm: z
        .number()
        .optional()
        .default(3000)
        .describe("Wall height in mm (default 3000)."),
      baseLevelMm: z
        .number()
        .optional()
        .describe("Base level elevation mm; default from active view."),
      pairingMode: z
        .enum(["centerline", "raw"])
        .optional()
        .default("centerline")
        .describe("centerline = merge double DWG lines; raw = use segments as-is."),
      dryRun: z
        .boolean()
        .optional()
        .default(false)
        .describe("If true, return planned axes without creating walls."),
      limit: z
        .number()
        .optional()
        .default(5000)
        .describe("Max CAD segments to read (default 5000)."),
    },
    async (args) => {
      if (!args.wallTypeId || args.wallTypeId <= 0) {
        return failResponse("wallTypeId is required — call get_available_family_types first.");
      }

      const toleranceMm = args.toleranceMm ?? 50;
      const mergeGapMm = args.mergeGapMm ?? toleranceMm;

      try {
        const result = await withRevitConnection(async (revitClient) => {
          const cadParams: Record<string, unknown> = {
            cadLinkName: args.cadLinkName ?? "",
            minLengthMm: args.minLengthMm ?? 300,
            limit: args.limit ?? 5000,
          };
          if (args.layerFilter !== undefined) {
            cadParams.layerFilter = args.layerFilter;
          }

          const cadRaw = (await revitClient.sendCommand(
            "get_cad_link_geometry",
            cadParams
          )) as CadGeometryResponse;

          if (!isSuccess(cadRaw) && cadRaw.ok !== true) {
            return {
              ok: false,
              summary:
                cadRaw.message ??
                cadRaw.summary ??
                "CAD не найден на виде — привяжите DWG к уровню.",
              count: 0,
              createdCount: 0,
              plannedCount: 0,
              items: [],
              availableLinks: (cadRaw as { availableLinks?: unknown }).availableLinks,
            };
          }

          const cadItems = cadRaw.items ?? [];
          if (cadItems.length === 0) {
            return {
              ok: false,
              summary: "CAD найден, но сегментов нет (проверьте layerFilter / видимость слоёв).",
              count: 0,
              createdCount: 0,
              plannedCount: 0,
              items: [],
            };
          }

          const traced = traceWallAxesFromCad(cadItems, {
            toleranceMm,
            mergeGapMm,
            minPairGapMm: args.minPairGapMm ?? 50,
            maxPairGapMm: args.maxPairGapMm ?? 500,
            minWallLengthMm: args.minWallLengthMm ?? 300,
            bboxMm: args.bboxMm,
            pairingMode: args.pairingMode ?? "centerline",
          });

          const verify = verifyAxesAgainstCad(
            traced.axes,
            cadItems,
            toleranceMm
          );

          if (traced.axes.length === 0) {
            return {
              ok: false,
              summary:
                "После обработки CAD нет осей стен (сузьте bbox, проверьте layerFilter или minWallLengthMm).",
              count: 0,
              createdCount: 0,
              plannedCount: 0,
              stats: traced.stats,
              verify,
              cadSummary: cadRaw.summary,
            };
          }

          if (args.dryRun) {
            return {
              ok: true,
              dryRun: true,
              summary: `Dry-run: ${traced.axes.length} осей стен из ${cadItems.length} CAD-сегментов`,
              count: traced.axes.length,
              plannedCount: traced.axes.length,
              createdCount: 0,
              axes: traced.axes,
              stats: traced.stats,
              verify,
              cadLinkName: cadRaw.cadLinkName,
              viewName: cadRaw.viewName,
            };
          }

          const viewInfo = (await revitClient.sendCommand(
            "get_current_view_info",
            {}
          )) as ViewInfoResponse;
          const baseLevel = args.baseLevelMm ?? readElevation(viewInfo);
          const heightMm = args.heightMm ?? 3000;

          const wallData = traced.axes.map((axis) => ({
            category: "OST_Walls",
            typeId: args.wallTypeId,
            locationLine: {
              p0: {
                x: axis.startMm.x,
                y: axis.startMm.y,
                z: axis.startMm.z ?? 0,
              },
              p1: {
                x: axis.endMm.x,
                y: axis.endMm.y,
                z: axis.endMm.z ?? 0,
              },
            },
            thickness: 0,
            height: heightMm,
            baseLevel,
            baseOffset: 0,
          }));

          const createResp = (await revitClient.sendCommand(
            "create_line_based_element",
            { data: wallData }
          )) as CreateWallResponse;

          const elementIds = extractElementIds(createResp);
          const createOk = isSuccess(createResp) || elementIds.length > 0;
          const errors = createResp.errors ?? [];
          const warnings = createResp.warnings ?? [];

          const createdCount = elementIds.length;
          const plannedCount = traced.axes.length;
          const failedCount = plannedCount - createdCount;

          return {
            ok: createOk && createdCount > 0,
            summary: createOk
              ? `Создано ${createdCount}/${plannedCount} стен по CAD` +
                (verify.failedAxes.length > 0
                  ? `; verify: max отклонение ${verify.maxDeviationMm} мм`
                  : `; verify OK (max ${verify.maxDeviationMm} мм)`)
              : createResp.message ??
                `Создано 0/${plannedCount} стен` +
                  (errors.length ? `: ${errors[0]}` : ""),
            count: createdCount,
            plannedCount,
            createdCount,
            failedCount,
            elementIds,
            axes: traced.axes,
            stats: traced.stats,
            verify,
            cadLinkName: cadRaw.cadLinkName,
            viewName: cadRaw.viewName ?? viewInfo.name ?? viewInfo.Name,
            errors,
            warnings,
            truncated: createdCount < plannedCount,
            message:
              failedCount > 0
                ? `Частичный успех: создано ${createdCount}/${plannedCount}. Проверьте errors.`
                : undefined,
          };
        });

        return {
          content: [{ type: "text" as const, text: JSON.stringify(result) }],
        };
      } catch (error) {
        return failResponse(
          error instanceof Error ? error.message : String(error)
        );
      }
    }
  );
}

function failResponse(message: string) {
  return {
    content: [
      {
        type: "text" as const,
        text: JSON.stringify({
          ok: false,
          summary: message,
          count: 0,
          createdCount: 0,
          plannedCount: 0,
        }),
      },
    ],
  };
}
