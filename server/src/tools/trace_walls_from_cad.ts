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
  type WallAxis,
  type WallTypeCandidate,
  type PointMm,
} from "../cad/cadWallTracing.js";
import {
  traceArcWallAxesFromCad,
  buildArcTraceHints,
  type ArcWallAxis,
} from "../cad/cadArcWallTracing.js";
import { traceOpeningsFromCad } from "../cad/cadOpeningTracing.js";
import {
  readWithLimitEscalation,
  truncationWarning,
} from "../cad/cadReadEscalation.js";
import { traceWallBandsFromCad } from "../cad/cadBandWallTracing.js";

/** A segment carrying arc metadata belongs to the curved-wall tracer, not the straight one. */
function isArcSegment(seg: CadSegment): boolean {
  return !!seg.arcCenterMm && !!seg.arcRadiusMm && seg.arcRadiusMm > 0;
}

/** Straight and curved axes travel together from here on; `arc` marks the curved ones. */
type TracedAxis = WallAxis & { arc?: ArcWallAxis };

/**
 * REV-153: centres of every door and window drawn on the CAD.
 *
 * Used to decide which wall gaps are openings. Judging a gap by width alone bridged an open
 * doorway on «Проект1» and produced an axis 192.9 mm off the underlay; and keeping the gaps
 * unbridged left the doors without a host, so each one got its own stub wall butted into the
 * run — visible as a seam either side of every door.
 */
function detectOpeningCentres(segments: CadSegment[]): PointMm[] {
  const centres: PointMm[] = [];
  for (const kind of ["door", "window"] as const) {
    try {
      const traced = traceOpeningsFromCad(segments, { kind });
      for (const o of traced.openings) centres.push(o.centerMm);
    } catch {
      // Opening detection is an optimisation here — wall tracing still works without it.
    }
  }
  return centres;
}

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

/**
 * REV-154: get a wall type of exactly this thickness, creating it if the project has none.
 * Returns null on any failure — a missing type is a reason to fall back, not to abort tracing.
 */
async function ensureWallType(
  revitClient: { sendCommand: (cmd: string, params: unknown) => Promise<unknown> },
  thicknessMm: number,
  sourceTypeId: number,
  toleranceMm: number
): Promise<{ thicknessMm: number; typeId: number; typeName: string } | null> {
  if (!sourceTypeId || sourceTypeId <= 0) return null;

  try {
    const raw = (await revitClient.sendCommand("ensure_wall_type", {
      thicknessMm,
      sourceTypeId,
      toleranceMm,
    })) as Record<string, unknown>;

    const body = (raw?.Response ?? raw?.response ?? raw) as Record<string, unknown>;
    const typeId = Number(body?.typeId ?? body?.TypeId ?? 0);
    if (!Number.isFinite(typeId) || typeId <= 0) return null;

    return {
      thicknessMm: Number(body?.thicknessMm ?? body?.ThicknessMm ?? thicknessMm),
      typeId,
      typeName: String(body?.typeName ?? body?.TypeName ?? `${thicknessMm}мм`),
    };
  } catch {
    return null;
  }
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
      openingGapMm: z
        .number()
        .min(0)
        .optional()
        .default(0)
        .describe(
          "Join collinear axes across gaps up to this width when both sides are paired and " +
            "equally thick — the breaks DWG leaves for doors, windows and curtain mullions. " +
            "Revit wants one continuous wall with openings cut into it, so ~2500 suits a flat " +
            "and removes most bridge walls in trace_openings_from_cad. 0 (default) keeps runs " +
            "split. Reported as stats.bridgedOpeningGaps."
        ),
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
        .enum(["centerline", "raw", "band"])
        .optional()
        .default("centerline")
        .describe(
          "centerline = pair each DWG face line with its nearest parallel neighbour (default; " +
            "right when a wall is drawn as two lines). band = group parallel lines into face " +
            "clusters and measure across the OUTER faces — use it when the DWG draws the " +
            "build-up (finish layers 12–25 mm apart), where centerline measures the gap " +
            "between two finish layers and invents thicknesses like 62.5 or 93.8 mm. " +
            "raw = use segments as-is."
        ),
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
      createMissingWallTypes: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "REV-154: when the project stocks no type within exactThicknessToleranceMm of a traced thickness, duplicate the nearest one at the exact mm (ensure_wall_type) instead of snapping to it. Needs autoMatchWallTypes."
        ),
      exactThicknessToleranceMm: z
        .number()
        .optional()
        .default(10)
        .describe(
          "How far a stock type may be from the traced thickness before createMissingWallTypes makes a new one (default 10 mm)."
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
              // REV-154: one chord per arc instead of a tessellated fan. A curved wall's
              // chords run ~100 mm each, so minLengthMm dropped them inside Revit and the
              // tracer never learned the arc existed — no wall, no warning, and the doors
              // on that wall silently lost their host.
              arcMode: "single",
            };
            if (args.layerFilter !== undefined) {
              cadParams.layerFilter = args.layerFilter;
            }
            return cadParams;
          };

          // REV-155: Revit truncates at `limit` before the layer filter runs, so on a large
          // DWG whole wall layers never arrive and the trace silently covers part of the plan.
          const readCad = (withModelLines: boolean) =>
            readWithLimitEscalation(
              (limit) =>
                revitClient.sendCommand("get_cad_link_geometry", {
                  ...buildCadParams(withModelLines),
                  limit,
                }) as Promise<CadGeometryResponse>,
              args.limit ?? 5000
            );

          let read = await readCad(includeModelLinesArg === true);
          let cadRaw = read.response;

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
              read = await readCad(true);
              cadRaw = read.response;
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

          // REV-154: arcs go to the curved-wall tracer. Left in the straight pipeline they are
          // either dropped (orthoOnly) or turned into a chord-shaped wall that cuts the corner.
          const arcSegmentsRaw = cadItemsRaw.filter(isArcSegment);
          const lineSegmentsRaw = cadItemsRaw.filter((s) => !isArcSegment(s));

          const preFiltered = filterSegmentsForWallTracing(lineSegmentsRaw, {
            excludeCadLinkPatterns: excludePatterns,
            excludeLayers: args.excludeLayers,
            hatchMinLengthMm: 1500,
            minLengthMm: args.minWallLengthMm ?? 300,
            orthoOnly,
          });

          // Same exclusions, but never by length or angle: an arc is measured by its sweep.
          const arcPreFiltered = filterSegmentsForWallTracing(arcSegmentsRaw, {
            excludeCadLinkPatterns: excludePatterns,
            excludeLayers: args.excludeLayers,
            hatchMinLengthMm: 0,
            minLengthMm: 0,
            orthoOnly: false,
          });

          let effectiveBbox = args.bboxMm;
          if (!effectiveBbox && (args.autoBbox ?? true)) {
            effectiveBbox = computeSegmentsBbox(
              [...preFiltered.segments, ...arcPreFiltered.segments],
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
                stats: {
                  input: preFiltered.segments.length,
                  excluded: 0,
                  nonOrthogonalDropped: 0,
                  arcsDropped: 0,
                },
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
            // REV-154: geometry the tracer cannot represent yet, or excluded by orthoOnly.
            // Silent loss here is what left two doors without a host on «Проект1».
            nonOrthogonalDropped: preFiltered.stats.nonOrthogonalDropped,
            arcsDropped: preFiltered.stats.arcsDropped,
            arcSegments: arcPreFiltered.segments.length,
          };

          const geometryHints: string[] = [];
          const readWarning = truncationWarning(read);
          if (readWarning) {
            geometryHints.push(readWarning);
          }
          if (filterStats.nonOrthogonalDropped > 0) {
            geometryHints.push(
              `${filterStats.nonOrthogonalDropped} линий(и) под углом отброшено фильтром ` +
                `orthoOnly. Если на плане есть диагональные стены — повторите с ` +
                `orthoOnly: false.`
            );
          }
          if (filterStats.arcsDropped > 0) {
            geometryHints.push(
              `${filterStats.arcsDropped} дуг(и) отброшено до трассировки — проверьте ` +
                `minLengthMm и excludeLayers.`
            );
          }

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

          // REV-153: find the door and window symbols on the *unfiltered* CAD, so a gap can
          // be bridged only where the drawing actually shows an opening. Wall layers are
          // filtered out of cadItems by then, and the openings live on their own layers.
          const openingCentres = args.openingGapMm
            ? detectOpeningCentres(cadItemsRaw)
            : undefined;

          // REV-156: band mode measures across the outer faces of a stack of parallel lines.
          // It replaces pairing wholesale, so it produces the same shape with the
          // pairing-specific counters left at zero — there is no "unpaired line" in a band.
          const traced =
            (args.pairingMode ?? "centerline") === "band"
              ? (() => {
                  const band = traceWallBandsFromCad(cadItems, {
                    minThicknessMm: args.minPairGapMm ?? 60,
                    maxThicknessMm: args.maxPairGapMm ?? 420,
                    minWallLengthMm: args.minWallLengthMm ?? 300,
                    bridgeGapMm: args.openingGapMm || 2600,
                  });
                  return {
                    axes: band.axes,
                    stats: {
                      inputSegments: band.stats.inputSegments,
                      afterBbox: band.stats.inputSegments,
                      afterPairing: band.axes.length,
                      afterMerge: band.axes.length,
                      pairedCount: band.axes.length,
                      unpairedSkipped: 0,
                      skippedShort: 0,
                      thicknessOutliersDropped: 0,
                      bridgedOpeningGaps: 0,
                      endCapsIgnored: 0,
                    },
                    thicknessClusters: band.thicknessClusters.map((c) => ({
                      thicknessMm: c.thicknessMm,
                      count: c.count,
                    })),
                    skipped: [],
                    hints: band.stats.skewed
                      ? [
                          `${band.stats.skewed} наклонных линий band-режим не трассирует — ` +
                            "для диагональных стен используйте pairingMode: centerline.",
                        ]
                      : [],
                  };
                })()
              : traceWallAxesFromCad(cadItems, {
                  toleranceMm,
                  mergeGapMm,
                  minPairGapMm: args.minPairGapMm ?? 55,
                  maxPairGapMm: args.maxPairGapMm ?? 280,
                  minWallLengthMm: args.minWallLengthMm ?? 300,
                  bboxMm: effectiveBbox,
                  pairingMode: args.pairingMode === "raw" ? "raw" : "centerline",
                  requirePair,
                  openingGapMm: args.openingGapMm ?? 0,
                  openingPointsMm: openingCentres,
                });

          const verify = verifyAxesAgainstCad(
            traced.axes,
            cadItems,
            toleranceMm
          );

          const straightAxes: TracedAxis[] =
            verify.failedAxes.length > 0
              ? traced.axes.filter(
                  (_, i) => !verify.failedAxes.some((f) => f.index === i)
                )
              : traced.axes;

          // REV-154: curved walls. Two concentric DWG arcs whose radii differ by the wall
          // thickness are the two faces of one wall; the centreline arc goes to Revit as
          // p0 / pointOnCurve / p1.
          const arcTrace = traceArcWallAxesFromCad(arcPreFiltered.segments, {
            minPairGapMm: args.minPairGapMm ?? 55,
            maxPairGapMm: args.maxPairGapMm ?? 280,
            minWallLengthMm: args.minWallLengthMm ?? 300,
            centerToleranceMm: toleranceMm,
            mergeGapMm,
            bboxMm: effectiveBbox,
          });
          const arcAxes: TracedAxis[] = arcTrace.axes.map((arc) => ({
            startMm: arc.startMm,
            endMm: arc.endMm,
            lengthMm: arc.lengthMm,
            sourceCadIds: arc.sourceCadIds,
            thicknessMm: arc.thicknessMm,
            paired: true,
            arc,
          }));

          const axesForCreate: TracedAxis[] = [...straightAxes, ...arcAxes];
          const arcHints = buildArcTraceHints(arcTrace, {
            minPairGapMm: args.minPairGapMm ?? 55,
            maxPairGapMm: args.maxPairGapMm ?? 280,
          });
          const curvedNote =
            arcAxes.length > 0 ? `; из них криволинейных: ${arcAxes.length}` : "";

          let wallTypes: WallTypeCandidate[] = [];
          if (autoMatchWallTypes) {
            try {
              wallTypes = await loadWallTypeCandidates(revitClient);
            } catch {
              wallTypes = [];
            }
          }

          // REV-154: a DWG measures 193 or 147 mm and the template stocks neither. Snapping to
          // the nearest stock type redraws the building at the wrong thickness, so make the type.
          const createdWallTypes: {
            thicknessMm: number;
            typeId: number;
            typeName: string;
          }[] = [];
          // dryRun previews; it must not leave new types behind in the project.
          if (autoMatchWallTypes && args.createMissingWallTypes && !args.dryRun) {
            const exactTol = args.exactThicknessToleranceMm ?? 10;
            const wanted = new Set<number>();
            for (const axis of axesForCreate) {
              const t = args.wallThicknessMm ?? axis.thicknessMm;
              if (t != null && t > 0) wanted.add(Math.round(t));
            }

            for (const thicknessMm of wanted) {
              if (matchWallTypeByThickness(thicknessMm, wallTypes, exactTol)) continue;

              const nearest = matchWallTypeByThickness(
                thicknessMm,
                wallTypes,
                Number.MAX_SAFE_INTEGER
              );
              const ensured = await ensureWallType(
                revitClient,
                thicknessMm,
                nearest?.typeId ?? args.wallTypeId,
                exactTol
              );
              if (!ensured) continue;

              wallTypes.push({
                typeId: ensured.typeId,
                name: ensured.typeName,
                thicknessMm: ensured.thicknessMm,
              });
              createdWallTypes.push(ensured);
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

          // REV-150: a dropped CAD line is the difference between «перечертил» and
          // «перечертил, но одну стену пропустил». Always surface what was skipped.
          const skippedSegments = traced.skipped.slice(0, 40);
          const skippedTruncated = traced.skipped.length > skippedSegments.length;
          const skippedByReason = traced.skipped.reduce<Record<string, number>>(
            (acc, s) => {
              acc[s.reason] = (acc[s.reason] ?? 0) + 1;
              return acc;
            },
            {}
          );

          if (axesForCreate.length === 0) {
            return {
              ok: false,
              summary:
                "После обработки нет осей стен (двойные линии не спарились). " +
                (traced.hints[0] ?? "Проверьте includeModelLines / minPairGapMm / requirePair."),
              count: 0,
              createdCount: 0,
              plannedCount: 0,
              stats: traced.stats,
              arcStats: arcTrace.stats,
              verify,
              filterStats,
              thicknessClusters: traced.thicknessClusters,
              hints: [...geometryHints, ...arcHints, ...traced.hints],
              skippedByReason,
              skippedSegments,
              skippedTruncated,
              cadSummary: cadRaw.summary,
            };
          }

          if (args.dryRun) {
            return {
              ok: true,
              dryRun: true,
              summary:
                `Dry-run: ${axesForCreate.length} осей стен${curvedNote}; толщины [${thicknessHint}] мм` +
                (verify.failedAxes.length > 0
                  ? ` (отброшено verify: ${verify.failedAxes.length})`
                  : "") +
                (traced.skipped.length > 0
                  ? `; ПРОПУЩЕНО линий CAD: ${traced.skipped.length} — см. hints / skippedSegments`
                  : ""),
              count: axesForCreate.length,
              plannedCount: axesForCreate.length,
              createdCount: 0,
              axes: axesForCreate,
              typeAssignments,
              droppedAxes: traced.axes.length - axesForCreate.length,
              stats: traced.stats,
              arcStats: arcTrace.stats,
              verify,
              filterStats,
              thicknessClusters: traced.thicknessClusters,
              hints: [...geometryHints, ...arcHints, ...traced.hints],
              skippedByReason,
              skippedSegments,
              skippedTruncated,
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
                // Curved wall: Revit rebuilds the DWG arc from three points.
                ...(axis.arc
                  ? {
                      pointOnCurve: {
                        x: axis.arc.midMm.x,
                        y: axis.arc.midMm.y,
                        z: axis.arc.midMm.z ?? 0,
                      },
                    }
                  : {}),
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
              ? `Создано ${createdCount}/${plannedCount} стен по CAD${curvedNote}; толщины [${thicknessHint}] мм` +
                (verify.failedAxes.length > 0
                  ? `; verify: max отклонение ${verify.maxDeviationMm} мм`
                  : `; verify OK (max ${verify.maxDeviationMm} мм)`) +
                (traced.skipped.length > 0
                  ? `; ПРОПУЩЕНО линий CAD: ${traced.skipped.length} — см. hints / skippedSegments`
                  : "")
              : `Создано 0/${plannedCount} стен` +
                  (errors.length ? `: ${errors[0]}` : ""),
            count: createdCount,
            plannedCount,
            createdCount,
            failedCount,
            elementIds,
            axes: axesForCreate,
            typeAssignments,
            createdWallTypes,
            droppedAxes: traced.axes.length - axesForCreate.length,
            stats: traced.stats,
            arcStats: arcTrace.stats,
            verify,
            filterStats,
            thicknessClusters: traced.thicknessClusters,
            hints: [...geometryHints, ...arcHints, ...traced.hints],
            skippedByReason,
            skippedSegments,
            skippedTruncated,
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
