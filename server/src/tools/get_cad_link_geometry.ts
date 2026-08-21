import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  readWithLimitEscalation,
  truncationWarning,
} from "../cad/cadReadEscalation.js";

type CadPoint = { x?: number; y?: number; z?: number };

type CadSegment = {
  startMm?: CadPoint;
  endMm?: CadPoint;
  layer?: string;
  lengthMm?: number;
  curveType?: string;
  arcId?: string;
  arcRadiusMm?: number;
};

type CadGeometryResponse = {
  items?: CadSegment[];
  blocks?: unknown[];
  count?: number;
  [key: string]: unknown;
};

type LayerStat = {
  layer: string;
  count: number;
  arcCount: number;
  totalLengthMm: number;
  minLengthMm: number;
  maxLengthMm: number;
  bboxMm: { minX: number; minY: number; maxX: number; maxY: number } | null;
};

/**
 * REV-152: a full DWG dump ran 72–96 KB — roughly 20k tokens — and the redraw flow pulled it
 * 13 times in one session. The layer table is what actually drives the next decision
 * (layerFilter, bboxMm); the coordinates only mattered to the trace_* tools, which read the
 * geometry over their own connection and never through this response.
 */
function summarize(response: CadGeometryResponse) {
  const items = Array.isArray(response.items) ? response.items : [];
  const byLayer = new Map<string, LayerStat>();

  for (const seg of items) {
    const layer = seg.layer ?? "(no layer)";
    let stat = byLayer.get(layer);
    if (!stat) {
      stat = {
        layer,
        count: 0,
        arcCount: 0,
        totalLengthMm: 0,
        minLengthMm: Number.POSITIVE_INFINITY,
        maxLengthMm: 0,
        bboxMm: null,
      };
      byLayer.set(layer, stat);
    }

    stat.count++;
    if (seg.arcId != null) stat.arcCount++;

    const len = typeof seg.lengthMm === "number" ? seg.lengthMm : 0;
    stat.totalLengthMm += len;
    stat.minLengthMm = Math.min(stat.minLengthMm, len);
    stat.maxLengthMm = Math.max(stat.maxLengthMm, len);

    for (const pt of [seg.startMm, seg.endMm]) {
      if (!pt || typeof pt.x !== "number" || typeof pt.y !== "number") continue;
      if (!stat.bboxMm) {
        stat.bboxMm = { minX: pt.x, minY: pt.y, maxX: pt.x, maxY: pt.y };
      } else {
        stat.bboxMm.minX = Math.min(stat.bboxMm.minX, pt.x);
        stat.bboxMm.minY = Math.min(stat.bboxMm.minY, pt.y);
        stat.bboxMm.maxX = Math.max(stat.bboxMm.maxX, pt.x);
        stat.bboxMm.maxY = Math.max(stat.bboxMm.maxY, pt.y);
      }
    }
  }

  const layers = [...byLayer.values()]
    .map((s) => ({
      ...s,
      minLengthMm: Number.isFinite(s.minLengthMm) ? Math.round(s.minLengthMm) : 0,
      maxLengthMm: Math.round(s.maxLengthMm),
      totalLengthMm: Math.round(s.totalLengthMm),
      bboxMm: s.bboxMm
        ? {
            minX: Math.round(s.bboxMm.minX),
            minY: Math.round(s.bboxMm.minY),
            maxX: Math.round(s.bboxMm.maxX),
            maxY: Math.round(s.bboxMm.maxY),
          }
        : null,
    }))
    .sort((a, b) => b.count - a.count);

  const { items: _items, blocks, ...envelope } = response;

  return {
    ...envelope,
    include: "summary",
    segmentCount: items.length,
    arcCount: items.filter((s) => s.arcId != null).length,
    blockCount: Array.isArray(blocks) ? blocks.length : 0,
    layers,
    sampleSegments: items.slice(0, 5),
    note:
      "Geometry omitted by default. trace_walls_from_cad / trace_openings_from_cad / " +
      "trace_columns_from_cad read it directly — pass include='segments' only to inspect the DWG by hand.",
  };
}

function pageSegments(
  response: CadGeometryResponse,
  offset: number,
  limit: number
) {
  const items = Array.isArray(response.items) ? response.items : [];
  const page = items.slice(offset, offset + limit);
  const { items: _items, ...envelope } = response;

  return {
    ...envelope,
    include: "segments",
    items: page,
    segmentsPagination: {
      total: items.length,
      offset,
      returned: page.length,
      hasMore: offset + page.length < items.length,
    },
  };
}

export function registerGetCadLinkGeometryTool(server: McpServer) {
  server.tool(
    "get_cad_link_geometry",
    "Read line/arc/polyline geometry from a DWG/CAD ImportInstance (link or import) " +
      "on the active floor plan. Returns segments in mm { startMm, endMm, layer, cadId } " +
      "plus bbox — use before tracing walls from CAD (REV-138). " +
      "If DWG was exploded, set includeModelLines=true to also read Model/Detail lines. " +
      "Fail-fast if no CAD is visible on the view. " +
      "This is DWG underlays only; for linked Revit models (АР/КР/ИОС .rvt) use get_linked_models.",
    {
      cadLinkName: z
        .string()
        .optional()
        .describe(
          "Optional CAD link/import name filter (substring, case-insensitive)."
        ),
      layerFilter: z
        .union([z.string(), z.array(z.string())])
        .optional()
        .describe(
          "Optional DWG layer name or list (substring match). Example: 'A-WALL' or ['WALL','Оси']."
        ),
      viewId: z
        .number()
        .optional()
        .describe("Optional view element id; default = active view."),
      minLengthMm: z
        .number()
        .optional()
        .default(0)
        .describe("Skip segments shorter than this length (mm). Default 0."),
      limit: z
        .number()
        .optional()
        .default(5000)
        .describe("Max segments to return (default 5000)."),
      includeHiddenLayers: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Include DWG layers hidden on the view (recommended true for wall tracing)."
        ),
      includeModelLines: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Also return ModelCurve/DetailCurve on the view (needed when DWG was exploded into model lines)."
        ),
      arcMode: z
        .enum(["tessellate", "single"])
        .optional()
        .default("tessellate")
        .describe(
          "How arcs are returned. 'tessellate' (default) splits each arc into chords. " +
            "'single' returns one chord per arc — use it for door swings, whose chords " +
            "otherwise fall under minLengthMm. Either way every chord carries arcId, " +
            "arcCenterMm and arcRadiusMm, so the original arc can be rebuilt."
        ),
      include: z
        .enum(["summary", "segments"])
        .optional()
        .default("summary")
        .describe(
          "'summary' (default) returns per-layer counts, lengths and bboxes plus a few sample " +
            "segments — enough to pick layerFilter/bboxMm. 'segments' returns the raw segment " +
            "list, which runs to tens of thousands of tokens on a real plan; page it with " +
            "segmentsOffset/segmentsLimit. The trace_* tools read the full geometry themselves, " +
            "so 'segments' is only for inspecting a DWG by hand."
        ),
      segmentsOffset: z
        .number()
        .int()
        .min(0)
        .optional()
        .default(0)
        .describe("include=segments: first segment to return."),
      segmentsLimit: z
        .number()
        .int()
        .min(1)
        .max(2000)
        .optional()
        .default(300)
        .describe("include=segments: how many segments to return (default 300, max 2000)."),
    },
    async (args) => {
      const params: Record<string, unknown> = {
        cadLinkName: args.cadLinkName ?? "",
        minLengthMm: args.minLengthMm ?? 0,
        limit: args.limit ?? 5000,
        includeHiddenLayers: args.includeHiddenLayers ?? false,
        includeModelLines: args.includeModelLines ?? false,
        arcMode: args.arcMode ?? "tessellate",
      };
      if (args.layerFilter !== undefined) {
        params.layerFilter = args.layerFilter;
      }
      if (args.viewId !== undefined) {
        params.viewId = args.viewId;
      }

      try {
        // REV-155: the per-layer table below is built from whatever Revit managed to read
        // before hitting `limit`, and the layer filter is applied to that same truncated
        // set. On «двг2.dwg» (19 011 segments) the default 5 000 hid the wall layer
        // entirely, so the summary said the drawing had no walls. Read it all first.
        const read = await withRevitConnection(async (revitClient) =>
          readWithLimitEscalation(
            (limit) =>
              revitClient.sendCommand("get_cad_link_geometry", {
                ...params,
                limit,
              }) as Promise<CadGeometryResponse>,
            (args.limit ?? 5000) as number
          )
        );
        const response = read.response;
        const warning = truncationWarning(read);

        const shaped =
          (args.include ?? "summary") === "segments"
            ? pageSegments(
                response as CadGeometryResponse,
                args.segmentsOffset ?? 0,
                args.segmentsLimit ?? 300
              )
            : summarize(response as CadGeometryResponse);
        if (warning) (shaped as Record<string, unknown>).warning = warning;

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(shaped),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `get_cad_link_geometry failed: ${
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
