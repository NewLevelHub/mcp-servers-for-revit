import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  traceWallAxesFromCad,
  verifyAxesAgainstCad,
  filterSegmentsForWallTracing,
  computeSegmentsBbox,
  matchWallTypeByThickness,
  parseWallTypeThicknessMm,
  DEFAULT_EXCLUDE_CAD_LINK_PATTERNS,
  type CadSegment,
  type BboxMm,
  type WallTypeCandidate,
  type PointMm,
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

type CreateWallResponse = {
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
  return (
    resp.Response ??
    resp.response ??
    resp.data ??
    resp.Data ??
    resp.elementIds ??
    []
  );
}

function extractErrors(resp: CreateWallResponse): string[] {
  if (resp.errors && resp.errors.length > 0) return resp.errors;
  const msg = resp.Message ?? resp.message ?? "";
  const marker = "Errors:";
  const idx = msg.indexOf(marker);
  if (idx < 0) return [];
  return msg
    .slice(idx + marker.length)
    .split("\n")
    .map((line) => line.replace(/^\s*•\s*/, "").trim())
    .filter((line) => line.length > 0);
}

function countModelLineSources(items: CadSegment[]): number {
  return items.filter(
    (s) =>
      s.source === "modelLine" ||
      s.source === "detailLine" ||
      (s.cadLinkName ?? "").toLowerCase() === "modellines"
  ).length;
}

/** Parse "x,y,z" with either "." or "," decimals from get_current_view_elements. */
function parseViewPointMm(raw: unknown): PointMm | null {
  if (!raw) return null;
  if (typeof raw === "object") {
    const o = raw as { x?: number; y?: number; z?: number };
    if (typeof o.x === "number" && typeof o.y === "number") {
      return { x: o.x, y: o.y, z: o.z ?? 0 };
    }
  }
  const str = String(raw);
  const parts = str.split(",").map((s) => s.trim());
  if (parts.length >= 6) {
    const x = Number(`${parts[0]}.${parts[1]}`);
    const y = Number(`${parts[2]}.${parts[3]}`);
    const z = Number(`${parts[4]}.${parts[5]}`);
    if ([x, y, z].every(Number.isFinite)) return { x, y, z };
  }
  if (parts.length >= 2) {
    const nums = parts.map((p) => Number(p.replace(",", ".")));
    if (nums.length >= 2 && nums.every(Number.isFinite)) {
      return { x: nums[0], y: nums[1], z: nums[2] ?? 0 };
    }
  }
  return null;
}

type ViewElementsResponse = {
  Elements?: Array<{
    Id?: number;
    Properties?: Record<string, unknown>;
  }>;
  elements?: Array<{
    Id?: number;
    Properties?: Record<string, unknown>;
  }>;
};

function modelLinesFromViewElements(resp: ViewElementsResponse): CadSegment[] {
  const elements = resp.Elements ?? resp.elements ?? [];
  const out: CadSegment[] = [];
  for (const el of elements) {
    const props = el.Properties ?? {};
    if (props.CurveType === "Unbound") continue;
    const start = parseViewPointMm(props.StartMm ?? props.Start);
    const end = parseViewPointMm(props.EndMm ?? props.End);
    if (!start || !end) continue;
    const lengthMm = Math.hypot(end.x - start.x, end.y - start.y);
    if (lengthMm < 0.1) continue;
    out.push({
      startMm: start,
      endMm: end,
      lengthMm,
      cadId: String(el.Id ?? out.length),
      layer: "modelLines",
      cadLinkName: "modelLines",
      source: "modelLine",
      curveType: "line",
    });
  }
  return out;
}

async function fetchModelLinesFallback(
  revitClient: { sendCommand: (cmd: string, params: unknown) => Promise<unknown> },
  minLengthMm: number
): Promise<CadSegment[]> {
  const raw = (await revitClient.sendCommand("get_current_view_elements", {
    modelCategoryList: ["OST_Lines"],
    includeHidden: true,
    limit: 2000,
  })) as ViewElementsResponse;
  return modelLinesFromViewElements(raw).filter(
    (s) => (s.lengthMm ?? 0) >= minLengthMm
  );
}

const CREATE_BATCH_SIZE = 15;

async function createWallsInBatches(
  revitClient: { sendCommand: (cmd: string, params: unknown) => Promise<unknown> },
  wallData: Record<string, unknown>[]
): Promise<{ elementIds: number[]; errors: string[]; warnings: string[] }> {
  const elementIds: number[] = [];
  const errors: string[] = [];
  const warnings: string[] = [];

  for (let i = 0; i < wallData.length; i += CREATE_BATCH_SIZE) {
    const chunk = wallData.slice(i, i + CREATE_BATCH_SIZE);
    const createResp = (await revitClient.sendCommand(
      "create_line_based_element",
      { data: chunk }
    )) as CreateWallResponse;

    elementIds.push(...extractElementIds(createResp));
    errors.push(...extractErrors(createResp));
    if (createResp.warnings) warnings.push(...createResp.warnings);
  }

  return { elementIds, errors, warnings };
}

async function loadWallTypeCandidates(
  revitClient: { sendCommand: (cmd: string, params: unknown) => Promise<unknown> }
): Promise<WallTypeCandidate[]> {
  const raw = (await revitClient.sendCommand("get_available_family_types", {
    categoryList: ["OST_Walls"],
  })) as FamilyTypeItem[] | { items?: FamilyTypeItem[]; Response?: FamilyTypeItem[] };

  const list = Array.isArray(raw)
    ? raw
    : raw.items ?? raw.Response ?? [];

  return list
    .map((t) => {
      const typeId = t.typeId ?? t.TypeId ?? 0;
      const name = t.name ?? t.Name ?? "";
      return {
        typeId,
        name,
        thicknessMm: parseWallTypeThicknessMm(name) ?? undefined,
      };
    })
    .filter((t) => t.typeId > 0);
}

export function registerTraceWallsFromCadTool(server: McpServer) {
  server.tool(
    "trace_walls_from_cad",
    "Trace walls from DWG/CAD on the active floor plan (REV-140). " +
      "Reads ImportInstance segments and (by default) Model/Detail lines when DWG was exploded. " +
      "Merges double lines to centerlines, measures thickness from face gap, " +
      "optionally auto-matches wall types by mm in type name. " +
      "Requires wallTypeId fallback from get_available_family_types. " +
      "Use dryRun to preview axes + detectedThicknesses without creating.",
    {
      wallTypeId: z
        .number()
        .int()
        .positive()
        .describe("Required fallback Wall type ElementId from get_available_family_types (OST_Walls)."),
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
        .default(55)
        .describe("Min distance between parallel DWG lines to pair (default 55 — skips dimension ticks ~40)."),
      maxPairGapMm: z
        .number()
        .optional()
        .default(280)
        .describe(
          "Max distance between parallel DWG lines to pair (default 280 — avoids wall×dimension false pairs)."
        ),
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
      requirePair: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "centerline mode: skip unpaired face lines (default true — avoids walls on each CAD face)."
        ),
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
      includeHiddenLayers: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Read DWG layers even if hidden on view (default true — needed for wall layers)."
        ),
      includeModelLines: z
        .union([z.boolean(), z.literal("auto")])
        .optional()
        .default("auto")
        .describe(
          "Include Model/Detail lines (exploded DWG). auto = on when ImportInstance has few wall segments (default)."
        ),
      orthoOnly: z
        .boolean()
        .optional()
        .default(true)
        .describe("Keep only axis-aligned segments (default true — drops door swings)."),
      excludeCadLinkPatterns: z
        .array(z.string())
        .optional()
        .describe(
          "CAD block name substrings to skip (furniture/doors). Default excludes chair/bed/desk/door blocks."
        ),
      excludeLayers: z
        .array(z.string())
        .optional()
        .describe("Layer substrings to skip entirely (optional)."),
      autoBbox: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "After furniture filter, clip to segment bbox + margin to drop mirrored far-away junk."
        ),
      bboxMarginMm: z
        .number()
        .optional()
        .default(500)
        .describe("Margin for autoBbox clip region (default 500 mm)."),
      wallThicknessMm: z
        .number()
        .optional()
        .describe(
          "Optional override thickness (informational). Prefer auto from double-line gap."
        ),
      autoMatchWallTypes: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Match wall type per axis by measured thickness vs mm in type name (fallback = wallTypeId)."
        ),
    },
    async (args) => {
      if (!args.wallTypeId || args.wallTypeId <= 0) {
        return failResponse("wallTypeId is required — call get_available_family_types first.");
      }

      const toleranceMm = args.toleranceMm ?? 50;
      const mergeGapMm = args.mergeGapMm ?? toleranceMm;
      const requirePair = args.requirePair ?? true;
      const orthoOnly = args.orthoOnly ?? true;
      const autoMatchWallTypes = args.autoMatchWallTypes ?? true;

      try {
        const result = await withRevitConnection(async (revitClient) => {
          const includeModelLinesArg = args.includeModelLines ?? "auto";
          let includeModelLines =
            includeModelLinesArg === true || includeModelLinesArg === "auto";

          const buildCadParams = (withModelLines: boolean) => {
            const cadParams: Record<string, unknown> = {
              cadLinkName: args.cadLinkName ?? "",
              minLengthMm: args.minLengthMm ?? 300,
              limit: args.limit ?? 5000,
              includeHiddenLayers: args.includeHiddenLayers ?? true,
              includeModelLines: withModelLines,
            };
            if (args.layerFilter !== undefined) {
              cadParams.layerFilter = args.layerFilter;
            }
            return cadParams;
          };

          let cadRaw = (await revitClient.sendCommand(
            "get_cad_link_geometry",
            buildCadParams(includeModelLinesArg === true)
          )) as CadGeometryResponse;

          // auto: if ImportInstance-only read is too thin for walls, re-read with model lines.
          if (includeModelLinesArg === "auto") {
            const rawItems = cadRaw.items ?? [];
            const modelCount = countModelLineSources(rawItems);
            const usablePreview = filterSegmentsForWallTracing(rawItems, {
              excludeCadLinkPatterns:
                args.excludeCadLinkPatterns ?? DEFAULT_EXCLUDE_CAD_LINK_PATTERNS,
              excludeLayers: args.excludeLayers,
              hatchMinLengthMm: 1500,
              minLengthMm: args.minWallLengthMm ?? 300,
              orthoOnly,
            });
            if (modelCount === 0 && usablePreview.segments.length < 20) {
              includeModelLines = true;
              // Prefer native includeModelLines (needs rebuilt commandset). If the
              // running Revit DLL ignores the flag, fall back to OST_Lines.
              cadRaw = (await revitClient.sendCommand(
                "get_cad_link_geometry",
                buildCadParams(true)
              )) as CadGeometryResponse;
              const afterNative = countModelLineSources(cadRaw.items ?? []);
              if (afterNative === 0) {
                try {
                  const lineSegs = await fetchModelLinesFallback(
                    revitClient,
                    args.minLengthMm ?? 300
                  );
                  if (lineSegs.length > 0) {
                    cadRaw = {
                      ...cadRaw,
                      ok: true,
                      items: [...(cadRaw.items ?? []), ...lineSegs],
                      count: (cadRaw.items?.length ?? 0) + lineSegs.length,
                      summary: `${cadRaw.summary ?? "CAD"} + modelLines×${lineSegs.length}`,
                    };
                  }
                } catch {
                  // keep CAD-only result
                }
              }
            }
          }

          if (!isSuccess(cadRaw) && cadRaw.ok !== true) {
            return {
              ok: false,
              summary:
                cadRaw.message ??
                cadRaw.summary ??
                "CAD не найден на виде — привяжите DWG к уровню (или includeModelLines при exploded DWG).",
              count: 0,
              createdCount: 0,
              plannedCount: 0,
              items: [],
              availableLinks: cadRaw.availableLinks,
              includeModelLines,
            };
          }

          const cadItemsRaw = cadRaw.items ?? [];
          if (cadItemsRaw.length === 0) {
            return {
              ok: false,
              summary:
                "Сегментов нет. Если DWG взорван — проверьте includeModelLines / видимость линий модели.",
              count: 0,
              createdCount: 0,
              plannedCount: 0,
              items: [],
              includeModelLines,
            };
          }

          const excludePatterns =
            args.excludeCadLinkPatterns ?? DEFAULT_EXCLUDE_CAD_LINK_PATTERNS;

          const preFiltered = filterSegmentsForWallTracing(cadItemsRaw, {
            excludeCadLinkPatterns: excludePatterns,
            excludeLayers: args.excludeLayers,
            hatchMinLengthMm: 1500,
            minLengthMm: args.minWallLengthMm ?? 300,
            orthoOnly,
          });

          let effectiveBbox = args.bboxMm;
          if (!effectiveBbox && (args.autoBbox ?? true)) {
            effectiveBbox = computeSegmentsBbox(
              preFiltered.segments,
              args.bboxMarginMm ?? 500
            );
          }

          const filtered = effectiveBbox
            ? filterSegmentsForWallTracing(preFiltered.segments, {
                bboxMm: effectiveBbox,
                excludeCadLinkPatterns: [],
                excludeLayers: [],
                hatchMinLengthMm: 0,
                minLengthMm: 0,
                orthoOnly: false,
              })
            : {
                segments: preFiltered.segments,
                stats: { input: preFiltered.segments.length, excluded: 0 },
              };

          const cadItems = filtered.segments;
          const filterStats = {
            rawSegments: cadItemsRaw.length,
            afterFurnitureFilter: preFiltered.segments.length,
            furnitureExcluded: preFiltered.stats.excluded,
            afterBbox: cadItems.length,
            bboxExcluded: filtered.stats.excluded,
            bboxMm: effectiveBbox,
            modelLineSegments: countModelLineSources(cadItemsRaw),
            includeModelLines,
          };

          if (cadItems.length === 0) {
            return {
              ok: false,
              summary:
                "После фильтрации мебели/bbox не осталось сегментов. " +
                "Для exploded DWG нужен includeModelLines=true.",
              count: 0,
              createdCount: 0,
              plannedCount: 0,
              filterStats,
              cadSummary: cadRaw.summary,
              layerSummary: cadRaw.layerSummary,
            };
          }

          const traced = traceWallAxesFromCad(cadItems, {
            toleranceMm,
            mergeGapMm,
            minPairGapMm: args.minPairGapMm ?? 55,
            maxPairGapMm: args.maxPairGapMm ?? 280,
            minWallLengthMm: args.minWallLengthMm ?? 300,
            bboxMm: effectiveBbox,
            pairingMode: args.pairingMode ?? "centerline",
            requirePair,
          });

          const verify = verifyAxesAgainstCad(
            traced.axes,
            cadItems,
            toleranceMm
          );

          const axesForCreate =
            verify.failedAxes.length > 0
              ? traced.axes.filter(
                  (_, i) => !verify.failedAxes.some((f) => f.index === i)
                )
              : traced.axes;

          let wallTypes: WallTypeCandidate[] = [];
          if (autoMatchWallTypes) {
            try {
              wallTypes = await loadWallTypeCandidates(revitClient);
            } catch {
              wallTypes = [];
            }
          }

          const typeAssignments = axesForCreate.map((axis) => {
            const measured =
              args.wallThicknessMm ??
              axis.thicknessMm ??
              traced.thicknessClusters[0]?.thicknessMm;
            let typeId = args.wallTypeId;
            let matchedName: string | undefined;
            if (autoMatchWallTypes && measured != null && wallTypes.length > 0) {
              const match = matchWallTypeByThickness(measured, wallTypes, 45);
              if (match) {
                typeId = match.typeId;
                matchedName = match.name;
              }
            }
            return {
              typeId,
              matchedName,
              thicknessMm: measured,
            };
          });

          const thicknessHint =
            traced.thicknessClusters.length > 0
              ? traced.thicknessClusters
                  .slice(0, 4)
                  .map((c) => `${Math.round(c.thicknessMm)}×${c.count}`)
                  .join(", ")
              : "n/a";

          if (axesForCreate.length === 0) {
            return {
              ok: false,
              summary:
                "После обработки нет осей стен (двойные линии не спарились). " +
                "Проверьте includeModelLines / minPairGapMm / requirePair.",
              count: 0,
              createdCount: 0,
              plannedCount: 0,
              stats: traced.stats,
              verify,
              filterStats,
              thicknessClusters: traced.thicknessClusters,
              cadSummary: cadRaw.summary,
            };
          }

          if (args.dryRun) {
            return {
              ok: true,
              dryRun: true,
              summary:
                `Dry-run: ${axesForCreate.length} осей стен; толщины [${thicknessHint}] мм` +
                (verify.failedAxes.length > 0
                  ? ` (отброшено verify: ${verify.failedAxes.length})`
                  : ""),
              count: axesForCreate.length,
              plannedCount: axesForCreate.length,
              createdCount: 0,
              axes: axesForCreate,
              typeAssignments,
              droppedAxes: traced.axes.length - axesForCreate.length,
              stats: traced.stats,
              verify,
              filterStats,
              thicknessClusters: traced.thicknessClusters,
              recommendedWallTypes: traced.thicknessClusters.slice(0, 5).map((c) => {
                const m = matchWallTypeByThickness(c.thicknessMm, wallTypes, 45);
                return {
                  thicknessMm: c.thicknessMm,
                  count: c.count,
                  typeId: m?.typeId ?? args.wallTypeId,
                  typeName: m?.name ?? "(fallback wallTypeId)",
                };
              }),
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

          const wallData = axesForCreate.map((axis, idx) => {
            const assign = typeAssignments[idx];
            const thicknessMm = assign.thicknessMm ?? args.wallThicknessMm ?? 200;
            return {
              category: "OST_Walls",
              typeId: assign.typeId,
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
              thickness: thicknessMm,
              height: heightMm,
              baseLevel,
              baseOffset: 0,
            };
          });

          const { elementIds, errors, warnings } = await createWallsInBatches(
            revitClient,
            wallData
          );

          const createdCount = elementIds.length;
          const plannedCount = axesForCreate.length;
          const failedCount = plannedCount - createdCount;
          const createOk = createdCount > 0;

          return {
            ok: createOk && createdCount > 0,
            summary: createOk
              ? `Создано ${createdCount}/${plannedCount} стен по CAD; толщины [${thicknessHint}] мм` +
                (verify.failedAxes.length > 0
                  ? `; verify: max отклонение ${verify.maxDeviationMm} мм`
                  : `; verify OK (max ${verify.maxDeviationMm} мм)`)
              : `Создано 0/${plannedCount} стен` +
                  (errors.length ? `: ${errors[0]}` : ""),
            count: createdCount,
            plannedCount,
            createdCount,
            failedCount,
            elementIds,
            axes: axesForCreate,
            typeAssignments,
            droppedAxes: traced.axes.length - axesForCreate.length,
            stats: traced.stats,
            verify,
            filterStats,
            thicknessClusters: traced.thicknessClusters,
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
