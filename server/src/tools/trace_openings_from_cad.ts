import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  type CadSegment,
  type CadBlock,
  type BboxMm,
  type PointMm,
  computeSegmentsBbox,
} from "../cad/cadWallTracing.js";
import {
  type HostWall,
  type OpeningKind,
  type OpeningTypeCandidate,
  type PlannedOpening,
  DEFAULT_DOOR_LAYER_PATTERNS,
  DEFAULT_WINDOW_LAYER_PATTERNS,
  DEFAULT_EXCLUDE_OPENING_LAYERS,
  traceOpeningsFromCad,
  matchOpeningsToHosts,
  matchOpeningTypeByWidth,
  parseOpeningTypeWidthMm,
  verifyOpeningsAgainstHosts,
} from "../cad/cadOpeningTracing.js";

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
  blocks?: CadBlock[];
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

type CreatePointResponse = {
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

type ViewElementsResponse = {
  Elements?: Array<{
    Id?: number;
    id?: number;
    Properties?: Record<string, unknown>;
  }>;
  elements?: Array<{
    Id?: number;
    id?: number;
    Properties?: Record<string, unknown>;
  }>;
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

function extractElementIds(resp: CreatePointResponse): number[] {
  return (
    resp.Response ??
    resp.response ??
    resp.data ??
    resp.Data ??
    resp.elementIds ??
    []
  );
}

async function resolveDonorWallTypeId(
  revitClient: { sendCommand: (cmd: string, params: unknown) => Promise<unknown> },
  donorWallId: number,
  cache: Map<number, number>
): Promise<number | null> {
  if (cache.has(donorWallId)) return cache.get(donorWallId)!;
  try {
    const raw = (await revitClient.sendCommand("get_element_parameters", {
      elementId: donorWallId,
      parameterNames: ["Тип", "Type", "Type Id", "TypeId"],
      slim: true,
    })) as {
      parameters?: Array<{ name?: string; displayValue?: string; value?: unknown }>;
      Parameters?: Array<{ name?: string; Name?: string; displayValue?: string; value?: unknown }>;
    };
    const params = raw.parameters ?? raw.Parameters ?? [];
    for (const p of params) {
      const name = (p.name ?? (p as { Name?: string }).Name ?? "").toLowerCase();
      const display = String(p.displayValue ?? p.value ?? "");
      if (name.includes("тип") || name === "type" || name.includes("typeid")) {
        const asNum = Number(display);
        if (Number.isFinite(asNum) && asNum > 0) {
          cache.set(donorWallId, asNum);
          return asNum;
        }
        // displayValue may be type name — resolve via family types
        const types = (await revitClient.sendCommand("get_available_family_types", {
          categoryList: ["OST_Walls"],
          familyNameFilter: display,
          limit: 20,
        })) as FamilyTypeItem[];
        const list = Array.isArray(types) ? types : [];
        const exact = list.find(
          (t) => (t.name ?? t.Name ?? "") === display
        );
        const typeId = exact?.typeId ?? exact?.TypeId;
        if (typeId && typeId > 0) {
          cache.set(donorWallId, typeId);
          return typeId;
        }
      }
    }
  } catch {
    /* fall through */
  }
  return null;
}

/**
 * Create short host walls across CAD door gaps (REV-148), then set hostWallId.
 * REV-149: type, height and base offset are cloned from the donor wall — a bridge with
 * a guessed 3000 mm height reads as a mistake on the plan even when the door is right.
 */
async function materializeBridgeWalls(
  revitClient: { sendCommand: (cmd: string, params: unknown) => Promise<unknown> },
  planned: PlannedOpening[],
  baseLevel: number,
  walls: HostWall[]
): Promise<{ planned: PlannedOpening[]; errors: string[]; bridgeWallIds: number[] }> {
  const out = [...planned];
  const errors: string[] = [];
  const bridgeWallIds: number[] = [];
  const typeCache = new Map<number, number>();
  const wallsById = new Map(walls.map((w) => [w.id, w]));

  for (let i = 0; i < out.length; i++) {
    const p = out[i];
    if (!p.bridge || p.hostWallId > 0) continue;

    const donor = wallsById.get(p.bridge.donorWallId);
    const typeId =
      donor?.typeId && donor.typeId > 0
        ? donor.typeId
        : await resolveDonorWallTypeId(
            revitClient,
            p.bridge.donorWallId,
            typeCache
          );
    if (!typeId) {
      errors.push(
        `Bridge for door @(${Math.round(p.centerMm.x)},${Math.round(p.centerMm.y)}): ` +
          `не удалось взять тип стены с donor ${p.bridge.donorWallId}`
      );
      continue;
    }

    const createResp = (await revitClient.sendCommand("create_line_based_element", {
      data: [
        {
          category: "OST_Walls",
          typeId,
          locationLine: {
            p0: {
              x: p.bridge.startMm.x,
              y: p.bridge.startMm.y,
              z: p.bridge.startMm.z ?? 0,
            },
            p1: {
              x: p.bridge.endMm.x,
              y: p.bridge.endMm.y,
              z: p.bridge.endMm.z ?? 0,
            },
          },
          // thickness is informational — the real value comes from typeId (WallType).
          thickness: donor?.widthMm ?? 90,
          height: donor?.heightMm && donor.heightMm > 0 ? donor.heightMm : 3000,
          baseLevel,
          baseOffset: donor?.baseOffsetMm ?? 0,
        },
      ],
    })) as CreatePointResponse;

    const ids = extractElementIds(createResp);
    if (ids.length === 0) {
      errors.push(
        ...extractErrors(createResp).length
          ? extractErrors(createResp)
          : [
              `Bridge wall create failed near (${Math.round(p.centerMm.x)},${Math.round(p.centerMm.y)})`,
            ]
      );
      continue;
    }

    bridgeWallIds.push(ids[0]);
    out[i] = { ...p, hostWallId: ids[0] };
  }

  return { planned: out, errors, bridgeWallIds };
}

export type PlacementCheckItem = {
  elementId: number;
  plannedCenterMm: PointMm;
  actualMm: PointMm | null;
  deviationMm: number | null;
  ok: boolean;
};

/** One created element id per planned opening, or null when that item failed. */
export type CreatedIdByPlan = Array<number | null>;

/**
 * REV-149: verify what Revit actually built, not what we planned.
 *
 * The old verify compared the plan against itself, so every silent relocation inside
 * Revit came back as a clean run. This reads the created elements back and reports the
 * real distance from the CAD swing centre.
 */
export async function verifyPlacedOpenings(
  revitClient: { sendCommand: (cmd: string, params: unknown) => Promise<unknown> },
  planned: PlannedOpening[],
  createdIds: CreatedIdByPlan,
  toleranceMm: number
): Promise<{
  ok: boolean;
  checked: number;
  failedCount: number;
  maxDeviationMm: number;
  items: PlacementCheckItem[];
  unreadable: number;
}> {
  const items: PlacementCheckItem[] = [];
  if (createdIds.every((id) => id == null)) {
    return { ok: true, checked: 0, failedCount: 0, maxDeviationMm: 0, items, unreadable: 0 };
  }

  const actualById = new Map<number, PointMm>();
  try {
    const resp = (await revitClient.sendCommand("get_current_view_elements", {
      modelCategoryList: ["OST_Doors", "OST_Windows"],
      includeHidden: false,
      limit: 2000,
    })) as ViewElementsResponse;
    for (const el of resp.Elements ?? resp.elements ?? []) {
      const id = el.Id ?? el.id;
      if (id == null) continue;
      const props = el.Properties ?? {};
      const x = parseNumber(props.LocationXMm ?? props.LocationX);
      const y = parseNumber(props.LocationYMm ?? props.LocationY);
      if (x != null && y != null) actualById.set(id, { x, y, z: 0 });
    }
  } catch {
    // Reported as unreadable below — never silently as a pass.
  }

  let maxDev = 0;
  let unreadable = 0;
  for (let i = 0; i < planned.length; i++) {
    const elementId = createdIds[i];
    const p = planned[i];
    // Items that were never created are counted by createdCount, not here.
    if (!p || elementId == null) continue;
    const actual = actualById.get(elementId) ?? null;
    if (!actual) {
      unreadable++;
      items.push({
        elementId,
        plannedCenterMm: p.centerMm,
        actualMm: null,
        deviationMm: null,
        ok: false,
      });
      continue;
    }
    const dev = Math.hypot(actual.x - p.centerMm.x, actual.y - p.centerMm.y);
    maxDev = Math.max(maxDev, dev);
    items.push({
      elementId,
      plannedCenterMm: p.centerMm,
      actualMm: actual,
      deviationMm: Math.round(dev * 10) / 10,
      ok: dev <= toleranceMm,
    });
  }

  const failedCount = items.filter((it) => !it.ok).length;
  return {
    ok: failedCount === 0,
    checked: items.length,
    failedCount,
    maxDeviationMm: Math.round(maxDev * 10) / 10,
    items,
    unreadable,
  };
}

function extractErrors(resp: CreatePointResponse): string[] {
  if (resp.errors && resp.errors.length > 0) return resp.errors;
  const msg = resp.Message ?? resp.message ?? "";
  const marker = "Errors:";
  const idx = msg.indexOf(marker);
  if (idx < 0) return msg ? [msg] : [];
  return msg
    .slice(idx + marker.length)
    .split("\n")
    .map((line) => line.replace(/^\s*•\s*/, "").trim())
    .filter((line) => line.length > 0);
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

function parseNumber(raw: unknown): number | undefined {
  if (raw == null) return undefined;
  const n = Number(String(raw).replace(",", "."));
  return Number.isFinite(n) ? n : undefined;
}

function wallsFromViewElements(resp: ViewElementsResponse): HostWall[] {
  const elements = resp.Elements ?? resp.elements ?? [];
  const out: HostWall[] = [];
  for (const el of elements) {
    const id = el.Id ?? el.id;
    if (id == null || id <= 0) continue;
    const props = el.Properties ?? {};
    const start = parseViewPointMm(props.StartMm ?? props.Start);
    const end = parseViewPointMm(props.EndMm ?? props.End);
    if (!start || !end) continue;
    const lengthMm = Math.hypot(end.x - start.x, end.y - start.y);
    if (lengthMm < 100) continue;
    out.push({
      id,
      startMm: start,
      endMm: end,
      lengthMm,
      // REV-149: bridge walls are cloned from a neighbour instead of guessed.
      typeId: parseNumber(props.WallTypeId),
      heightMm: parseNumber(props.WallHeightMm),
      widthMm: parseNumber(props.WallWidthMm),
      baseOffsetMm: parseNumber(props.WallBaseOffsetMm),
    });
  }
  return out;
}

async function loadOpeningTypeCandidates(
  revitClient: { sendCommand: (cmd: string, params: unknown) => Promise<unknown> },
  category: "OST_Doors" | "OST_Windows"
): Promise<OpeningTypeCandidate[]> {
  const raw = (await revitClient.sendCommand("get_available_family_types", {
    categoryList: [category],
  })) as FamilyTypeItem[] | { items?: FamilyTypeItem[]; Response?: FamilyTypeItem[] };

  const list = Array.isArray(raw) ? raw : raw.items ?? raw.Response ?? [];
  return list
    .map((t) => {
      const typeId = t.typeId ?? t.TypeId ?? 0;
      const name = t.name ?? t.Name ?? "";
      return {
        typeId,
        name,
        widthMm: parseOpeningTypeWidthMm(name) ?? undefined,
      };
    })
    .filter((t) => t.typeId > 0);
}

const CREATE_BATCH_SIZE = 1; // one-per-call: same-host batch auto-spaces and ignores XY

async function createOpeningsInBatches(
  revitClient: { sendCommand: (cmd: string, params: unknown) => Promise<unknown> },
  data: Record<string, unknown>[]
): Promise<{
  elementIds: number[];
  /** Aligned to data[]: the created id, or null when that item failed. */
  createdIds: CreatedIdByPlan;
  errors: string[];
  warnings: string[];
}> {
  const elementIds: number[] = [];
  // Kept index-aligned with data[] so a single failure does not shift every
  // later opening onto the wrong element when verifying placement.
  const createdIds: CreatedIdByPlan = new Array(data.length).fill(null);
  const errors: string[] = [];
  const warnings: string[] = [];

  for (let i = 0; i < data.length; i += CREATE_BATCH_SIZE) {
    const chunk = data.slice(i, i + CREATE_BATCH_SIZE);
    const createResp = (await revitClient.sendCommand(
      "create_point_based_element",
      { data: chunk }
    )) as CreatePointResponse;

    const ids = extractElementIds(createResp);
    elementIds.push(...ids);
    // One-per-call, so the mapping back to the plan index is unambiguous.
    for (let k = 0; k < ids.length && i + k < data.length; k++) {
      createdIds[i + k] = ids[k];
    }

    errors.push(...extractErrors(createResp));
    if (createResp.warnings) warnings.push(...createResp.warnings);
    if (!isSuccess(createResp) && ids.length === 0) {
      const msg = createResp.Message ?? createResp.message;
      if (msg && !errors.includes(msg)) errors.push(msg);
    }
  }

  return { elementIds, createdIds, errors, warnings };
}

function normalizeLayerFilter(
  layerFilter: string | string[] | undefined,
  defaults: string[]
): string[] {
  if (layerFilter == null) return defaults;
  const arr = Array.isArray(layerFilter) ? layerFilter : [layerFilter];
  const cleaned = arr.map((s) => String(s).trim()).filter(Boolean);
  return cleaned.length > 0 ? cleaned : defaults;
}

function assignTypes(
  planned: PlannedOpening[],
  fallbackTypeId: number,
  types: OpeningTypeCandidate[],
  autoMatch: boolean
): PlannedOpening[] {
  return planned.map((p) => {
    let typeId = fallbackTypeId;
    let matchedTypeName: string | undefined;
    if (autoMatch && types.length > 0) {
      const m = matchOpeningTypeByWidth(p.widthMm, types, 80);
      if (m) {
        typeId = m.typeId;
        matchedTypeName = m.name;
      }
    }
    return { ...p, typeId, matchedTypeName };
  });
}

function plannedSummaryItems(planned: PlannedOpening[]) {
  return planned.map((p) => ({
    kind: p.kind,
    centerMm: p.centerMm,
    locationMm: p.locationMm,
    widthMm: p.widthMm,
    hostWallId: p.hostWallId,
    hostDistanceMm: p.hostDistanceMm,
    alongOvershootMm: p.alongOvershootMm,
    typeId: p.typeId,
    matchedTypeName: p.matchedTypeName,
    layer: p.layer,
    paramT: p.paramT,
    // REV-149: "arc" means position/width/swing/hand came from the DWG swing itself;
    // "leaf"/"cluster" mean the old heuristics ran and the result needs a look.
    detection: p.detection,
    hingeMm: p.hingeMm,
    swingNormal: p.swingNormal,
    bridge: p.bridge
      ? {
          kind: p.bridge.kind,
          donorWallId: p.bridge.donorWallId,
          leftWallId: p.bridge.leftWallId,
          rightWallId: p.bridge.rightWallId,
          startMm: p.bridge.startMm,
          endMm: p.bridge.endMm,
        }
      : undefined,
  }));
}

type CategoryBucket = {
  plannedCount: number;
  createdCount: number;
  failedCount: number;
  unmatchedCount: number;
  items: ReturnType<typeof plannedSummaryItems>;
  verify?: ReturnType<typeof verifyOpeningsAgainstHosts>;
  /** REV-149: read back from Revit after creation — the honest check. */
  placement?: Awaited<ReturnType<typeof verifyPlacedOpenings>>;
  elementIds: number[];
};

export function registerTraceOpeningsFromCadTool(server: McpServer) {
  server.tool(
    "trace_openings_from_cad",
    "Trace doors and/or windows from DWG/CAD onto existing host walls (REV-147/148). " +
      "Reads CAD layers (A-DOOR / A-GLAZ…), detects door leaf (prefer MCUT), matches host wall " +
      "or bridges gaps between short wall segments (WC stalls), then create_point_based_element. " +
      "Requires doorTypeId and/or windowTypeId. Curtain glazing (A-GLAZ-CURT) is excluded — " +
      "use trace_walls_from_cad for витраж. Prefer dryRun first. Walls must already exist on the plan.",
    {
      category: z
        .enum(["door", "window", "both"])
        .optional()
        .default("both")
        .describe("Which openings to place: door | window | both (default both)."),
      doorTypeId: z
        .number()
        .int()
        .positive()
        .optional()
        .describe(
          "Required when category is door|both. Door FamilySymbol id from get_available_family_types (OST_Doors)."
        ),
      windowTypeId: z
        .number()
        .int()
        .positive()
        .optional()
        .describe(
          "Required when category is window|both. Window FamilySymbol id from get_available_family_types (OST_Windows)."
        ),
      cadLinkName: z
        .string()
        .optional()
        .describe("Optional CAD link name filter (substring)."),
      layerFilter: z
        .union([z.string(), z.array(z.string())])
        .optional()
        .describe(
          "Override layer filter for the active category. Defaults: doors A-DOOR/DOOR/Двер; windows A-GLAZ/WINDOW/Окна (not A-GLAZ-CURT)."
        ),
      doorLayerFilter: z
        .union([z.string(), z.array(z.string())])
        .optional()
        .describe("Door-only layer override when category=both."),
      windowLayerFilter: z
        .union([z.string(), z.array(z.string())])
        .optional()
        .describe("Window-only layer override when category=both."),
      excludeLayers: z
        .array(z.string())
        .optional()
        .describe("Layer substrings to skip (default includes A-GLAZ-CURT)."),
      bboxMm: bboxSchema
        .optional()
        .describe("Clip region in mm {minX,maxX,minY,maxY}."),
      maxHostDistanceMm: z
        .number()
        .optional()
        .default(250)
        .describe(
          "Max perpendicular distance from CAD leaf to host wall centerline (default 250; REV-148)."
        ),
      minOpeningWidthMm: z
        .number()
        .optional()
        .default(600)
        .describe("Min opening width from CAD bbox (default 600)."),
      maxOpeningWidthMm: z
        .number()
        .optional()
        .default(2500)
        .describe("Max opening width from CAD bbox (default 2500)."),
      toleranceMm: z
        .number()
        .optional()
        .default(100)
        .describe(
          "Verify: max CAD leaf center → placement distance mm (default 100; REV-148)."
        ),
      strictLocation: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "REV-149: place each opening exactly at the CAD point. Revit's end-clamping, " +
            "junction nudging and batch auto-spacing are disabled; an opening that cannot be " +
            "honored fails loudly instead of being moved. Set false only to fall back to the " +
            "old forgiving behaviour."
        ),
      strictToleranceMm: z
        .number()
        .optional()
        .default(50)
        .describe("REV-149: max mm an opening may drift from the CAD point in strict mode (default 50)."),
      useArcs: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "REV-149: derive doors from DWG swing arcs (hinge = arc centre) — exact position, " +
            "width, swing side and hand. Set false only when swings were exploded into polylines."
        ),
      allowBridge: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "If true, create short host wall across gaps between prostenki when CAD door has no continuous host (default true, REV-148)."
        ),
      sillHeightMm: z
        .number()
        .optional()
        .default(900)
        .describe("Window baseOffset / sill height mm (default 900)."),
      clusterGapMm: z
        .number()
        .optional()
        .default(450)
        .describe("Max gap to cluster CAD segments into one opening symbol (default 450)."),
      autoMatchTypesByWidth: z
        .boolean()
        .optional()
        .default(true)
        .describe("Match door/window type by mm width in type name (fallback = doorTypeId/windowTypeId)."),
      dryRun: z
        .boolean()
        .optional()
        .default(false)
        .describe("If true, return planned openings + hostWallId without creating."),
      includeModelLines: z
        .union([z.boolean(), z.literal("auto")])
        .optional()
        .default(false)
        .describe(
          "Include Model/Detail lines. Default false for linked DWG with A-DOOR layers; set true for exploded."
        ),
      includeHiddenLayers: z
        .boolean()
        .optional()
        .default(true)
        .describe("Read hidden DWG layers (default true)."),
      autoBbox: z
        .boolean()
        .optional()
        .default(false)
        .describe("Clip to CAD segment bbox + margin (default false for openings)."),
      bboxMarginMm: z
        .number()
        .optional()
        .default(500)
        .describe("Margin for autoBbox (default 500)."),
      limit: z
        .number()
        .optional()
        .default(5000)
        .describe("Max CAD segments to read (default 5000)."),
      minLengthMm: z
        .number()
        .optional()
        .default(50)
        .describe("Min CAD segment length for get_cad_link_geometry (default 50 — keep door ticks)."),
      baseLevelMm: z
        .number()
        .optional()
        .describe("Base level elevation mm; default from active view."),
    },
    async (args) => {
      const category = args.category ?? "both";
      const needDoors = category === "door" || category === "both";
      const needWindows = category === "window" || category === "both";

      if (needDoors && (!args.doorTypeId || args.doorTypeId <= 0)) {
        return failResponse(
          "doorTypeId is required when category is door|both — call get_available_family_types (OST_Doors)."
        );
      }
      if (needWindows && (!args.windowTypeId || args.windowTypeId <= 0)) {
        return failResponse(
          "windowTypeId is required when category is window|both — call get_available_family_types (OST_Windows)."
        );
      }

      const toleranceMm = args.toleranceMm ?? 100;
      const maxHostDistanceMm = args.maxHostDistanceMm ?? 250;
      const allowBridge = args.allowBridge ?? true;
      const strictLocation = args.strictLocation ?? true;
      const strictToleranceMm = args.strictToleranceMm ?? 50;
      const useArcs = args.useArcs ?? true;
      const autoMatch = args.autoMatchTypesByWidth ?? true;
      const sillHeightMm = args.sillHeightMm ?? 900;
      const excludeLayers = args.excludeLayers ?? DEFAULT_EXCLUDE_OPENING_LAYERS;
      const matchOpts = {
        maxHostDistanceMm,
        allowBridge,
      };

      try {
        const result = await withRevitConnection(async (revitClient) => {
          const includeModelLines =
            args.includeModelLines === true || args.includeModelLines === "auto";

          const cadParams: Record<string, unknown> = {
            cadLinkName: args.cadLinkName ?? "",
            minLengthMm: args.minLengthMm ?? 50,
            limit: args.limit ?? 5000,
            includeHiddenLayers: args.includeHiddenLayers ?? true,
            includeModelLines,
            // REV-149: one chord per arc keeps small swings above minLengthMm and carries
            // the arc centre/radius, which is what makes hinge + hand exact.
            arcMode: useArcs ? "single" : "tessellate",
          };

          // Broad read — filter by opening layers in TS (door+window may differ).
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
              plannedCount: 0,
              createdCount: 0,
              doors: emptyBucket(),
              windows: emptyBucket(),
              availableLinks: cadRaw.availableLinks,
            };
          }

          let cadItems = cadRaw.items ?? [];
          if (cadItems.length === 0) {
            return {
              ok: false,
              summary: "Сегментов CAD нет. Проверьте связь DWG / слои проёмов (A-DOOR, A-GLAZ).",
              plannedCount: 0,
              createdCount: 0,
              doors: emptyBucket(),
              windows: emptyBucket(),
              layerSummary: cadRaw.layerSummary,
            };
          }

          let effectiveBbox = args.bboxMm;
          if (!effectiveBbox && (args.autoBbox ?? false)) {
            effectiveBbox = computeSegmentsBbox(cadItems, args.bboxMarginMm ?? 500);
          }
          if (effectiveBbox) {
            cadItems = cadItems.filter((s) => {
              const xs = [s.startMm.x, s.endMm.x];
              const ys = [s.startMm.y, s.endMm.y];
              return !(
                Math.max(...xs) < effectiveBbox!.minX ||
                Math.min(...xs) > effectiveBbox!.maxX ||
                Math.max(...ys) < effectiveBbox!.minY ||
                Math.min(...ys) > effectiveBbox!.maxY
              );
            });
          }

          const wallsRaw = (await revitClient.sendCommand("get_current_view_elements", {
            modelCategoryList: ["OST_Walls"],
            includeHidden: false,
            limit: 2000,
          })) as ViewElementsResponse;
          const walls = wallsFromViewElements(wallsRaw);

          if (walls.length === 0) {
            return {
              ok: false,
              summary:
                "На виде нет стен-host. Сначала trace_walls_from_cad / create_line_based_element, потом проёмы.",
              plannedCount: 0,
              createdCount: 0,
              doors: emptyBucket(),
              windows: emptyBucket(),
              cadSummary: cadRaw.summary,
            };
          }

          let doorTypes: OpeningTypeCandidate[] = [];
          let windowTypes: OpeningTypeCandidate[] = [];
          if (autoMatch) {
            try {
              if (needDoors) {
                doorTypes = await loadOpeningTypeCandidates(revitClient, "OST_Doors");
              }
              if (needWindows) {
                windowTypes = await loadOpeningTypeCandidates(
                  revitClient,
                  "OST_Windows"
                );
              }
            } catch {
              // fallback typeIds only
            }
          }

          const doorLayers = normalizeLayerFilter(
            args.doorLayerFilter ?? (category === "door" ? args.layerFilter : undefined),
            DEFAULT_DOOR_LAYER_PATTERNS
          );
          const windowLayers = normalizeLayerFilter(
            args.windowLayerFilter ??
              (category === "window" ? args.layerFilter : undefined),
            DEFAULT_WINDOW_LAYER_PATTERNS
          );

          const traceOptsBase = {
            excludeLayers,
            clusterGapMm: args.clusterGapMm ?? 450,
            minOpeningWidthMm: args.minOpeningWidthMm ?? 600,
            maxOpeningWidthMm: args.maxOpeningWidthMm ?? 2500,
            bboxMm: effectiveBbox,
            blocks: cadRaw.blocks ?? [],
            useArcs,
          };

          type TraceStats = ReturnType<typeof traceOpeningsFromCad>["stats"];

          let doorPlanned: PlannedOpening[] = [];
          let doorUnmatched = 0;
          let doorStats: TraceStats | undefined;
          if (needDoors) {
            const traced = traceOpeningsFromCad(cadItems, {
              kind: "door",
              layerPatterns: doorLayers,
              ...traceOptsBase,
            });
            doorStats = traced.stats;
            const matched = matchOpeningsToHosts(traced.openings, walls, matchOpts);
            doorUnmatched = matched.unmatched.length;
            doorPlanned = assignTypes(
              matched.planned,
              args.doorTypeId!,
              doorTypes,
              autoMatch
            );
          }

          let windowPlanned: PlannedOpening[] = [];
          let windowUnmatched = 0;
          let windowStats: TraceStats | undefined;
          if (needWindows) {
            const traced = traceOpeningsFromCad(cadItems, {
              kind: "window",
              layerPatterns: windowLayers,
              ...traceOptsBase,
            });
            windowStats = traced.stats;
            const matched = matchOpeningsToHosts(traced.openings, walls, matchOpts);
            windowUnmatched = matched.unmatched.length;
            windowPlanned = assignTypes(
              matched.planned,
              args.windowTypeId!,
              windowTypes,
              autoMatch
            );
          }

          const doorVerify = verifyOpeningsAgainstHosts(
            doorPlanned,
            walls,
            toleranceMm
          );
          const windowVerify = verifyOpeningsAgainstHosts(
            windowPlanned,
            walls,
            toleranceMm
          );

          const plannedCount = doorPlanned.length + windowPlanned.length;

          if (plannedCount === 0) {
            return {
              ok: false,
              dryRun: args.dryRun ?? false,
              summary:
                "Проёмов с host не найдено. Проверьте слои A-DOOR / A-GLAZ на DWG и что стены уже созданы.",
              plannedCount: 0,
              createdCount: 0,
              doors: {
                ...emptyBucket(),
                unmatchedCount: doorUnmatched,
              },
              windows: {
                ...emptyBucket(),
                unmatchedCount: windowUnmatched,
              },
              wallCount: walls.length,
              doorStats,
              windowStats,
              layerSummary: cadRaw.layerSummary,
              cadSummary: cadRaw.summary,
            };
          }

          if (args.dryRun) {
            const bridgeCount =
              doorPlanned.filter((p) => p.bridge).length +
              windowPlanned.filter((p) => p.bridge).length;
            return {
              ok: true,
              dryRun: true,
              summary:
                `Dry-run: двери ${doorPlanned.length} (host), окна ${windowPlanned.length} (host)` +
                (bridgeCount > 0 ? `; bridge/stub: ${bridgeCount}` : "") +
                (doorUnmatched + windowUnmatched > 0
                  ? `; без host: ${doorUnmatched + windowUnmatched}`
                  : ""),
              plannedCount,
              createdCount: 0,
              bridgeCount,
              doors: {
                plannedCount: doorPlanned.length,
                createdCount: 0,
                failedCount: 0,
                unmatchedCount: doorUnmatched,
                items: plannedSummaryItems(doorPlanned),
                verify: doorVerify,
                elementIds: [],
              },
              windows: {
                plannedCount: windowPlanned.length,
                createdCount: 0,
                failedCount: 0,
                unmatchedCount: windowUnmatched,
                items: plannedSummaryItems(windowPlanned),
                verify: windowVerify,
                elementIds: [],
              },
              wallCount: walls.length,
              doorStats,
              windowStats,
              cadLinkName: cadRaw.cadLinkName,
              viewName: cadRaw.viewName,
            };
          }

          const viewInfo = (await revitClient.sendCommand(
            "get_current_view_info",
            {}
          )) as ViewInfoResponse;
          const baseLevel = args.baseLevelMm ?? readElevation(viewInfo);

          const doorBridges = await materializeBridgeWalls(
            revitClient,
            doorPlanned,
            baseLevel,
            walls
          );
          doorPlanned = doorBridges.planned;
          const windowBridges = await materializeBridgeWalls(
            revitClient,
            windowPlanned,
            baseLevel,
            walls
          );
          windowPlanned = windowBridges.planned;

          // Drop openings that still lack a host after failed bridge
          const doorReady = doorPlanned.filter((p) => p.hostWallId > 0);
          const windowReady = windowPlanned.filter((p) => p.hostWallId > 0);

          const toCreateData = (
            planned: PlannedOpening[],
            kind: OpeningKind
          ): Record<string, unknown>[] =>
            planned.map((p) => {
              // Offset into CAD swing side. create_point_based_element must keep this
              // off-wall point for auto-facing (REV-147); centerline snap alone loses the side.
              const sn = p.swingNormal;
              const ox = sn ? sn.x * 120 : 0;
              const oy = sn ? sn.y * 120 : 0;

              // REV-149: hinge side along the wall, so Revit can un-mirror the hand.
              const hd = p.hingeDir;
              const handHintPoint = hd
                ? {
                    x: p.locationMm.x + hd.x * Math.max(p.widthMm / 2, 100),
                    y: p.locationMm.y + hd.y * Math.max(p.widthMm / 2, 100),
                    z: p.locationMm.z ?? 0,
                  }
                : undefined;

              return {
                name: kind === "door" ? "door from CAD" : "window from CAD",
                typeId: p.typeId,
                locationPoint: {
                  x: p.locationMm.x + ox,
                  y: p.locationMm.y + oy,
                  z: p.locationMm.z ?? 0,
                },
                width: p.widthMm,
                height: kind === "door" ? 2100 : 1500,
                baseLevel,
                baseOffset: kind === "window" ? sillHeightMm : 0,
                hostWallId: p.hostWallId,
                // REV-149: place exactly where the DWG says or fail loudly. Without this
                // Revit's end-clamping and junction-nudging silently move the opening.
                strictLocation,
                strictToleranceMm,
                handHintPoint,
              };
            });

          const doorData = toCreateData(doorReady, "door");
          const windowData = toCreateData(windowReady, "window");

          const emptyCreate = {
            elementIds: [] as number[],
            createdIds: [] as CreatedIdByPlan,
            errors: [] as string[],
            warnings: [] as string[],
          };
          const doorCreate =
            doorData.length > 0
              ? await createOpeningsInBatches(revitClient, doorData)
              : emptyCreate;
          const windowCreate =
            windowData.length > 0
              ? await createOpeningsInBatches(revitClient, windowData)
              : emptyCreate;

          const doorCreated = doorCreate.elementIds.length;
          const windowCreated = windowCreate.elementIds.length;
          const createdCount = doorCreated + windowCreated;
          const errors = [
            ...doorBridges.errors,
            ...windowBridges.errors,
            ...doorCreate.errors,
            ...windowCreate.errors,
          ];
          const warnings = [...doorCreate.warnings, ...windowCreate.warnings];
          const elementIds = [
            ...doorCreate.elementIds,
            ...windowCreate.elementIds,
          ];
          const bridgeWallIds = [
            ...doorBridges.bridgeWallIds,
            ...windowBridges.bridgeWallIds,
          ];

          // REV-149: the plan-vs-plan check is kept as context, but the verdict now comes
          // from what Revit actually built.
          const doorPlacement = await verifyPlacedOpenings(
            revitClient,
            doorReady,
            doorCreate.createdIds,
            toleranceMm
          );
          const windowPlacement = await verifyPlacedOpenings(
            revitClient,
            windowReady,
            windowCreate.createdIds,
            toleranceMm
          );

          const doorsBucket: CategoryBucket = {
            plannedCount: doorPlanned.length,
            createdCount: doorCreated,
            failedCount: Math.max(0, doorPlanned.length - doorCreated),
            unmatchedCount: doorUnmatched,
            items: plannedSummaryItems(doorPlanned),
            verify: verifyOpeningsAgainstHosts(doorReady, walls, toleranceMm),
            placement: doorPlacement,
            elementIds: doorCreate.elementIds,
          };
          const windowsBucket: CategoryBucket = {
            plannedCount: windowPlanned.length,
            createdCount: windowCreated,
            failedCount: Math.max(0, windowPlanned.length - windowCreated),
            unmatchedCount: windowUnmatched,
            items: plannedSummaryItems(windowPlanned),
            verify: verifyOpeningsAgainstHosts(windowReady, walls, toleranceMm),
            placement: windowPlacement,
            elementIds: windowCreate.elementIds,
          };

          const misplacedCount =
            doorPlacement.items.filter((it) => it.deviationMm != null && !it.ok).length +
            windowPlacement.items.filter((it) => it.deviationMm != null && !it.ok).length;
          const unreadableCount = doorPlacement.unreadable + windowPlacement.unreadable;

          return {
            // Openings that landed off the underlay are not a success, even though the
            // elements exist — that is exactly what hid the REV-147 misplacements.
            ok: createdCount > 0 && misplacedCount === 0,
            summary:
              createdCount > 0
                ? `Создано дверей ${doorCreated}/${doorPlanned.length}, окон ${windowCreated}/${windowPlanned.length}` +
                  (bridgeWallIds.length
                    ? `; bridge walls ${bridgeWallIds.length}`
                    : "") +
                  `; факт. отклонение от DWG: двери max ${doorPlacement.maxDeviationMm} мм` +
                  `, окна max ${windowPlacement.maxDeviationMm} мм` +
                  (misplacedCount > 0
                    ? `; ВНЕ ДОПУСКА ${misplacedCount} (допуск ${toleranceMm} мм)`
                    : "") +
                  (unreadableCount > 0
                    ? `; не удалось перепроверить ${unreadableCount}`
                    : "")
                : `Создано 0/${plannedCount} проёмов` +
                  (errors.length ? `: ${errors[0]}` : ""),
            plannedCount,
            createdCount,
            failedCount: plannedCount - createdCount,
            elementIds,
            bridgeWallIds,
            doors: doorsBucket,
            windows: windowsBucket,
            wallCount: walls.length,
            doorStats,
            windowStats,
            cadLinkName: cadRaw.cadLinkName,
            viewName: cadRaw.viewName ?? viewInfo.name ?? viewInfo.Name,
            errors,
            warnings,
            misplacedCount,
            unreadableCount,
            strictLocation,
            truncated: createdCount < plannedCount,
            message:
              misplacedCount > 0
                ? `${misplacedCount} проём(ов) стоят дальше ${toleranceMm} мм от DWG — см. doors.placement / windows.placement.`
                : createdCount < plannedCount
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

function emptyBucket(): CategoryBucket {
  return {
    plannedCount: 0,
    createdCount: 0,
    failedCount: 0,
    unmatchedCount: 0,
    items: [],
    elementIds: [],
  };
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
          doors: emptyBucket(),
          windows: emptyBucket(),
        }),
      },
    ],
  };
}
