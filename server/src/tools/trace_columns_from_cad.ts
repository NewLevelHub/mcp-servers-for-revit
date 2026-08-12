import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  type CadSegment,
  type CadBlock,
  type BboxMm,
  computeSegmentsBbox,
} from "../cad/cadWallTracing.js";
import {
  readWithLimitEscalation,
  truncationWarning,
} from "../cad/cadReadEscalation.js";
import {
  type ColumnTypeCandidate,
  type DetectedColumn,
  DEFAULT_COLUMN_LAYER_PATTERNS,
  DEFAULT_EXCLUDE_COLUMN_LAYERS,
  matchColumnTypeBySize,
  parseColumnTypeSizeMm,
  traceColumnsFromCad,
} from "../cad/cadColumnTracing.js";

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
  items?: CadSegment[];
  blocks?: CadBlock[];
  bboxMm?: BboxMm;
  cadLinkName?: string;
  viewName?: string;
  layerSummary?: unknown;
  availableLinks?: unknown;
};

type ViewInfoResponse = {
  elevation?: number;
  levelElevation?: number;
  LevelElevation?: number;
  name?: string;
  Name?: string;
};

type CreateResponse = {
  success?: boolean;
  Success?: boolean;
  data?: number[];
  Data?: number[];
  Response?: number[];
  response?: number[];
  elementIds?: number[];
  warnings?: string[];
  errors?: string[];
  message?: string;
  Message?: string;
};

type FamilyTypeItem = {
  typeId?: number;
  TypeId?: number;
  name?: string;
  Name?: string;
};

function isSuccess(resp: { success?: boolean; Success?: boolean; ok?: boolean }): boolean {
  return resp.success === true || resp.Success === true || resp.ok === true;
}

function extractElementIds(resp: CreateResponse): number[] {
  return (
    resp.Response ?? resp.response ?? resp.data ?? resp.Data ?? resp.elementIds ?? []
  );
}

function extractErrors(resp: CreateResponse): string[] {
  if (resp.errors && resp.errors.length > 0) return resp.errors;
  const msg = resp.Message ?? resp.message ?? "";
  const marker = "Errors:";
  const idx = msg.indexOf(marker);
  // REV-154: a clean run answers "Successfully created 3 element(s)." — echoing that back
  // as an error made every good result read as "создано 3/3; ошибок 1".
  if (idx < 0) return msg && !isSuccess(resp) ? [msg] : [];
  return msg
    .slice(idx + marker.length)
    .split("\n")
    .map((line) => line.replace(/^\s*•\s*/, "").trim())
    .filter((line) => line.length > 0);
}

function readElevation(view: ViewInfoResponse): number {
  return view.elevation ?? view.levelElevation ?? view.LevelElevation ?? 0;
}

async function loadColumnTypes(
  revitClient: { sendCommand: (cmd: string, params: unknown) => Promise<unknown> },
  category: string
): Promise<ColumnTypeCandidate[]> {
  const raw = (await revitClient.sendCommand("get_available_family_types", {
    categoryList: [category],
  })) as FamilyTypeItem[] | { items?: FamilyTypeItem[]; Response?: FamilyTypeItem[] };

  const list = Array.isArray(raw) ? raw : raw.items ?? raw.Response ?? [];
  return list
    .map((t) => {
      const typeId = t.typeId ?? t.TypeId ?? 0;
      const name = t.name ?? t.Name ?? "";
      const size = parseColumnTypeSizeMm(name);
      return {
        typeId,
        name,
        widthMm: size?.widthMm,
        depthMm: size?.depthMm,
      };
    })
    .filter((t) => t.typeId > 0);
}

function columnSummaryItems(
  columns: DetectedColumn[],
  typeIds: number[],
  typeNames: Array<string | undefined>
) {
  return columns.map((c, i) => ({
    centerMm: c.centerMm,
    widthMm: c.widthMm,
    depthMm: c.depthMm,
    rotationDeg: c.rotationDeg,
    shape: c.shape,
    layer: c.layer,
    blockName: c.blockName,
    segmentCount: c.segmentCount,
    typeId: typeIds[i],
    matchedTypeName: typeNames[i],
  }));
}

function failResponse(message: string) {
  return {
    content: [
      {
        type: "text" as const,
        text: JSON.stringify({
          ok: false,
          summary: message,
          plannedCount: 0,
          createdCount: 0,
          columns: [],
        }),
      },
    ],
  };
}

export function registerTraceColumnsFromCadTool(server: McpServer) {
  server.tool(
    "trace_columns_from_cad",
    "Trace structural/architectural columns from DWG/CAD onto the plan (REV-149). " +
      "Reads column layers (S-COLS / A-COLS / Колонны), groups each symbol by DWG block " +
      "instance, measures it in its own frame (so rotated columns keep their real size) " +
      "and places point-based columns with rotation via create_point_based_element. " +
      "Round columns are detected from arc metadata. Use this instead of trace_walls_from_cad " +
      "for columns — a column traced as walls becomes four stubs. Prefer dryRun first.",
    {
      columnTypeId: z
        .number()
        .int()
        .positive()
        .optional()
        .describe(
          "Fallback column FamilySymbol id from get_available_family_types. " +
            "Required unless autoMatchTypesBySize finds a match for every column."
        ),
      category: z
        .enum(["structural", "architectural"])
        .optional()
        .default("structural")
        .describe(
          "structural → OST_StructuralColumns (default), architectural → OST_Columns."
        ),
      cadLinkName: z
        .string()
        .optional()
        .describe("Optional CAD link name filter (substring)."),
      layerFilter: z
        .union([z.string(), z.array(z.string())])
        .optional()
        .describe(
          "Override the column layer filter. Default: S-COL / A-COL / COLUMN / Колонн."
        ),
      excludeLayers: z
        .array(z.string())
        .optional()
        .describe("Layer substrings to skip (default drops annotation/grid-tag layers)."),
      bboxMm: bboxSchema.optional().describe("Clip region in mm {minX,maxX,minY,maxY}."),
      minSizeMm: z
        .number()
        .optional()
        .default(150)
        .describe("Min column side / diameter (default 150)."),
      maxSizeMm: z
        .number()
        .optional()
        .default(1500)
        .describe("Max column side / diameter (default 1500)."),
      maxAspectRatio: z
        .number()
        .optional()
        .default(3)
        .describe(
          "Reject shapes longer than this ratio — those are walls, not columns (default 3)."
        ),
      heightMm: z
        .number()
        .optional()
        .default(3000)
        .describe("Column height in mm (default 3000)."),
      autoMatchTypesBySize: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Match a column type whose name carries the section (400x400, D400). " +
            "A 300x600 type also matches a 600x300 column — same section rotated."
        ),
      dryRun: z
        .boolean()
        .optional()
        .default(false)
        .describe("If true, return detected columns without creating anything."),
      includeHiddenLayers: z
        .boolean()
        .optional()
        .default(true)
        .describe("Read hidden DWG layers (default true)."),
      includeModelLines: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include Model/Detail lines (set true for exploded DWG)."),
      autoBbox: z
        .boolean()
        .optional()
        .default(false)
        .describe("Clip to CAD segment bbox + margin (default false)."),
      bboxMarginMm: z.number().optional().default(500).describe("Margin for autoBbox."),
      limit: z
        .number()
        .optional()
        .default(5000)
        .describe("Max CAD segments to read (default 5000)."),
      minLengthMm: z
        .number()
        .optional()
        .default(20)
        .describe("Min CAD segment length (default 20 — column sides are short)."),
      baseLevelMm: z
        .number()
        .optional()
        .describe("Base level elevation mm; default from active view."),
    },
    async (args) => {
      const dryRun = args.dryRun ?? false;
      const autoMatch = args.autoMatchTypesBySize ?? true;
      const revitCategory =
        (args.category ?? "structural") === "structural"
          ? "OST_StructuralColumns"
          : "OST_Columns";

      try {
        const result = await withRevitConnection(async (revitClient) => {
          // REV-155: Revit truncates at `limit` and filters layers afterwards, so on a big
          // DWG the columns in the unread half never arrive. Escalate until nothing is left.
          const read = await readWithLimitEscalation(
            (limit) =>
              revitClient.sendCommand("get_cad_link_geometry", {
                cadLinkName: args.cadLinkName ?? "",
                minLengthMm: args.minLengthMm ?? 20,
                limit,
                includeHiddenLayers: args.includeHiddenLayers ?? true,
                includeModelLines: args.includeModelLines ?? false,
                // Round columns are circles — keep the arc metadata intact.
                arcMode: "single",
              }) as Promise<CadGeometryResponse>,
            args.limit ?? 5000
          );
          const cadRaw = read.response;
          const readWarning = truncationWarning(read);

          if (!isSuccess(cadRaw) && cadRaw.ok !== true) {
            return {
              ok: false,
              summary:
                cadRaw.message ??
                cadRaw.summary ??
                "CAD не найден на виде — привяжите DWG к уровню.",
              plannedCount: 0,
              createdCount: 0,
              columns: [],
              availableLinks: cadRaw.availableLinks,
            };
          }

          let cadItems = cadRaw.items ?? [];
          if (cadItems.length === 0) {
            return {
              ok: false,
              summary:
                "Сегментов CAD нет. Проверьте связь DWG и слои колонн (S-COLS, A-COLS, Колонны).",
              plannedCount: 0,
              createdCount: 0,
              columns: [],
              layerSummary: cadRaw.layerSummary,
            };
          }

          let effectiveBbox = args.bboxMm;
          if (!effectiveBbox && (args.autoBbox ?? false)) {
            effectiveBbox = computeSegmentsBbox(cadItems, args.bboxMarginMm ?? 500);
          }

          const layerPatterns = args.layerFilter
            ? (Array.isArray(args.layerFilter) ? args.layerFilter : [args.layerFilter])
                .map((s) => String(s).trim())
                .filter(Boolean)
            : DEFAULT_COLUMN_LAYER_PATTERNS;

          const traced = traceColumnsFromCad(cadItems, {
            layerPatterns,
            excludeLayers: args.excludeLayers ?? DEFAULT_EXCLUDE_COLUMN_LAYERS,
            minSizeMm: args.minSizeMm ?? 150,
            maxSizeMm: args.maxSizeMm ?? 1500,
            maxAspectRatio: args.maxAspectRatio ?? 3,
            bboxMm: effectiveBbox,
            blocks: cadRaw.blocks ?? [],
          });

          if (traced.columns.length === 0) {
            return {
              ok: false,
              summary:
                "Колонн не найдено. Проверьте слои колонн на DWG (layerFilter) и диапазон minSizeMm/maxSizeMm.",
              plannedCount: 0,
              createdCount: 0,
              columns: [],
              stats: traced.stats,
              layerSummary: cadRaw.layerSummary,
            };
          }

          let types: ColumnTypeCandidate[] = [];
          if (autoMatch) {
            try {
              types = await loadColumnTypes(revitClient, revitCategory);
            } catch {
              // fall back to columnTypeId
            }
          }

          const typeIds: number[] = [];
          const typeNames: Array<string | undefined> = [];
          const unmatchedSizes: string[] = [];
          for (const c of traced.columns) {
            const match = autoMatch
              ? matchColumnTypeBySize(c.widthMm, c.depthMm, types)
              : null;
            if (match) {
              typeIds.push(match.typeId);
              typeNames.push(match.name);
            } else {
              typeIds.push(args.columnTypeId ?? 0);
              typeNames.push(undefined);
              if (!args.columnTypeId) unmatchedSizes.push(`${c.widthMm}×${c.depthMm}`);
            }
          }

          const items = columnSummaryItems(traced.columns, typeIds, typeNames);

          if (unmatchedSizes.length > 0) {
            return {
              ok: false,
              summary:
                `Не подобран тип для ${unmatchedSizes.length} колонн(ы): ` +
                `${[...new Set(unmatchedSizes)].slice(0, 5).join(", ")}. ` +
                "Передайте columnTypeId или загрузите семейство нужного сечения.",
              plannedCount: traced.columns.length,
              createdCount: 0,
              columns: items,
              stats: traced.stats,
              availableTypes: types.slice(0, 20).map((t) => t.name),
            };
          }

          if (dryRun) {
            return {
              ok: true,
              dryRun: true,
              summary:
                `Dry-run: найдено колонн ${traced.columns.length} ` +
                `(прямоугольных ${traced.columns.filter((c) => c.shape === "rectangular").length}, ` +
                `круглых ${traced.columns.filter((c) => c.shape === "round").length})`,
              plannedCount: traced.columns.length,
              createdCount: 0,
              columns: items,
              stats: traced.stats,
              warnings: readWarning ? [readWarning] : [],
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

          const data = traced.columns.map((c, i) => ({
            name: "column from CAD",
            category: revitCategory,
            typeId: typeIds[i],
            locationPoint: { x: c.centerMm.x, y: c.centerMm.y, z: c.centerMm.z ?? 0 },
            width: c.widthMm,
            depth: c.depthMm,
            height: heightMm,
            baseLevel,
            baseOffset: 0,
            // Columns are not hosted, so create_point_based_element applies rotation.
            rotation: c.rotationDeg,
          }));

          const createResp = (await revitClient.sendCommand(
            "create_point_based_element",
            { data }
          )) as CreateResponse;

          const elementIds = extractElementIds(createResp);
          const errors = extractErrors(createResp);
          const createdCount = elementIds.length;

          return {
            ok: createdCount > 0 && createdCount === traced.columns.length,
            summary:
              createdCount > 0
                ? `Создано колонн ${createdCount}/${traced.columns.length}` +
                  (errors.length ? `; ошибок ${errors.length}` : "")
                : `Создано 0/${traced.columns.length} колонн` +
                  (errors.length ? `: ${errors[0]}` : ""),
            plannedCount: traced.columns.length,
            createdCount,
            failedCount: traced.columns.length - createdCount,
            elementIds,
            columns: items,
            stats: traced.stats,
            errors,
            warnings: [
              ...(readWarning ? [readWarning] : []),
              ...(createResp.warnings ?? []),
            ],
            cadLinkName: cadRaw.cadLinkName,
            viewName: cadRaw.viewName ?? viewInfo.name ?? viewInfo.Name,
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
