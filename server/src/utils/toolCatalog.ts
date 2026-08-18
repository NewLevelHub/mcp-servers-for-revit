/**
 * Which tools the `lite` profile lists, and how the rest are grouped so a caller
 * can ask for exactly the ones a task needs (REV-41).
 *
 * The full catalog serialises to ~194 KB of JSON schema — roughly 51k tokens the
 * model reads before writing its first character, on every single turn. An
 * architect asking "сколько помещений на этаже" waits through all of it.
 *
 * `lite` lists {@link LITE_TOOLS} only. `lite+sheets,annotation` lists those plus
 * the named groups, so a drawing-layout turn pays for sheet and annotation tools
 * and not for CAD tracing and norm checks it will never call.
 *
 * ## Why composition happens at startup, not mid-session
 *
 * The obvious design — one always-listed tool that un-hides a group on demand —
 * does not work. `RegisteredTool.enable()` does emit
 * `notifications/tools/list_changed`, but no current client acts on it: Cursor
 * (IDE and CLI) and Claude Code both snapshot the catalog when the session
 * starts, and a newly enabled tool comes back as "not found" / "disabled" for the
 * rest of that session. This was measured once during REV-157 against the Cursor
 * SDK and confirmed again on 2026-08-18 against the clients' own issue trackers.
 * So the tool list a client sees is fixed at connect time, and the only lever is
 * which tools are listed *then* — hence the profile string.
 *
 * The assistant-bridge is the caller that benefits: it already picks a profile
 * per turn and hands it to a freshly created agent, which is a fresh connection.
 *
 * Adding a tool file without listing it here fails `toolCatalog.test.ts`: an
 * unassigned tool would silently vanish from `lite` with no group to bring it back.
 */

/** Listed in every profile, including `lite`. */
export const LITE_TOOLS: ReadonlySet<string> = new Set([
  // Look before you touch.
  "get_current_view_info",
  "get_current_view_elements",
  "get_selected_elements",
  "get_element_parameters",
  "get_elements_parameters",
  "get_available_family_types",
  "get_document_styles",
  "analyze_model_statistics",
  "export_room_data",
  // Everyday modelling.
  "create_line_based_element",
  "create_point_based_element",
  "create_surface_based_element",
  "create_room",
  "create_level",
  "create_text_note",
  "ensure_wall_type",
  "number_rooms",
  "set_element_parameter",
  // The batch form has to be listed wherever the singular one is: its description
  // tells the model to prefer the batch, and a request with no heavy-task hint
  // never escalates past `lite`, so pointing at a hidden tool would cost a turn.
  "set_elements_parameters",
  "operate_element",
  "delete_element",
  // Cheap round-trip saver: without it the model fires the same read five times
  // over, which is part of the "долго обрабатывает" the architects reported.
  "batch_execute",
]);

/**
 * Group names, in the order `expand_toolset` lists them. Declared as a tuple so
 * the tool can hand them straight to `z.enum` — a plain `string[]` would leave
 * the model with a free-text field and no idea what is on offer.
 */
export const TOOL_GROUP_NAMES = [
  "norms",
  "quality",
  "schedules",
  "sheets",
  "annotation",
  "cad",
  "modeling",
  "advanced",
] as const;

export type ToolGroupName = (typeof TOOL_GROUP_NAMES)[number];

/**
 * Groups revealed by `expand_toolset`. Keys are what the model passes as `groups`,
 * so they read as tasks ("sheets", "norms"), not as source folders.
 */
export const TOOL_GROUPS: Readonly<Record<ToolGroupName, readonly string[]>> = {
  /** Norm control: the checks, the model readers they feed on, the rule library. */
  norms: [
    "check_accessibility",
    "check_door_width",
    "check_evacuation_distance",
    "check_evacuation_width",
    "check_fire_doors",
    "check_min_dimensions",
    "check_room_depth",
    "check_room_norms",
    "check_tambour_size",
    "check_vertical_circulation",
    "check_window_openings",
    "run_norm_audit",
    "apply_norm_result",
    "annotate_norm_findings",
    "query_norm_rules",
    "save_norm_rule",
    "extract_norm_rules_from_pdf",
    // Hot in the logs, but only ever called to feed a check.
    "get_door_egress_info",
    "get_opening_geometry_info",
    "get_room_geometry_metrics",
    "get_vertical_circulation_info",
  ],

  /**
   * Model health before issue — what Revit itself flagged, and what is still
   * blank on the sheets. Kept apart from `norms`: these answer "is the model in
   * good shape", not "does it meet СП/ГОСТ", and an architect asks them at
   * different moments.
   */
  quality: ["get_model_warnings", "check_sheet_readiness"],

  /** Schedules, ведомости, and the bulk exports that back them. */
  schedules: [
    "create_schedule",
    "configure_schedule",
    "get_schedule_definition",
    "validate_schedule",
    "create_door_schedule",
    "create_window_schedule",
    "create_floor_schedule",
    "create_curtain_wall_schedule",
    "create_finish_schedule",
    "create_floor_explication",
    "fit_schedule_to_sheet",
    "export_room_finish_data",
    "export_apartment_data",
    "get_material_quantities",
  ],

  /** Sheets, title blocks and ТЭП — laying drawings out. */
  sheets: [
    "create_sheet",
    "place_view_on_sheet",
    "auto_layout_sheet",
    "fill_title_block",
    "render_tep_table",
    "export_tep_data",
    "create_detail_view",
  ],

  /** Dimensions, tags, text and 2D detailing on a view. */
  annotation: [
    "create_dimensions",
    "dimension_grids",
    "dimension_room_walls",
    "tag_elements",
    "tag_all_rooms",
    "tag_all_walls",
    "create_text_notes",
    "color_elements",
    "create_detail_lines",
    "create_filled_regions",
    "create_detail_regions",
    "place_detail_component",
    "create_node_detail",
  ],

  /** Redrawing a DWG underlay into Revit geometry. */
  cad: [
    "get_cad_link_geometry",
    "trace_walls_from_cad",
    "trace_openings_from_cad",
    "trace_columns_from_cad",
  ],

  /** Structure and circulation the everyday set does not cover. */
  modeling: [
    "create_grid",
    "configure_grid_display",
    "create_stair",
    "create_railing",
    "create_floor_opening",
    "create_structural_framing_system",
    "ensure_opening_type",
    "load_family",
    "ai_element_filter",
  ],

  /** Escape hatches, kept apart so neither is reached for by accident. */
  advanced: ["send_code_to_revit", "say_hello"],
};

/** One-liners `expand_toolset` shows so the model can pick without guessing. */
export const TOOL_GROUP_SUMMARIES: Readonly<Record<ToolGroupName, string>> = {
  norms:
    "norm checks — fire doors, evacuation width/distance, room depth, min dimensions, accessibility — plus the rule library",
  quality:
    "model health before issue — Revit's own warnings, and blank/duplicate штамп fields on sheets",
  schedules:
    "schedules and ведомости (doors, windows, floors, finishes) plus bulk data export",
  sheets: "sheets, title blocks, view placement, auto-layout, ТЭП table",
  annotation: "dimensions, tags, text notes, filled regions, node details",
  cad: "tracing walls, openings and columns from a DWG underlay",
  modeling: "grids, stairs, railings, floor openings, framing, family loading",
  advanced: "raw code execution and the connection smoke test",
};

/** Every tool hidden in `lite`, flattened. */
export function allGroupedTools(): Set<string> {
  const all = new Set<string>();
  for (const tools of Object.values(TOOL_GROUPS)) {
    for (const tool of tools) all.add(tool);
  }
  return all;
}

/** Which group a tool belongs to, or undefined for a lite tool. */
export function groupForTool(tool: string): ToolGroupName | undefined {
  return TOOL_GROUP_NAMES.find((group) => TOOL_GROUPS[group].includes(tool));
}

// --- profile parsing --------------------------------------------------------

export type ToolHandle = { enable(): void; disable(): void };

export interface ParsedToolProfile {
  /** The profile `register.ts` switches on. */
  base: string;
  /** Groups to list on top of {@link LITE_TOOLS}. Only meaningful for `lite`. */
  groups: ToolGroupName[];
  /** Group names that were asked for but do not exist — logged, not fatal. */
  unknownGroups: string[];
}

/**
 * Read `MCP_TOOL_PROFILE`. `lite+sheets,annotation` means "the everyday set plus
 * those two groups"; `lite+all` is every group, i.e. the same listing as
 * `default`. Separators `+` and `,` are interchangeable so neither spelling is a
 * silent mistake.
 *
 * An unknown group is dropped rather than thrown: a typo in an env var should
 * cost the model that group, not the whole Revit connection.
 */
export function parseToolProfile(raw: string | undefined): ParsedToolProfile {
  const text = (raw ?? "default").trim().toLowerCase();
  const [baseText, ...rest] = text.split(/[+,]/);
  const base = baseText.trim() || "default";

  const requested = rest.map((part) => part.trim()).filter(Boolean);
  const wantsAll = requested.includes("all");

  const groups: ToolGroupName[] = [];
  const unknownGroups: string[] = [];

  for (const name of wantsAll ? TOOL_GROUP_NAMES : requested) {
    if (TOOL_GROUP_NAMES.includes(name as ToolGroupName)) {
      if (!groups.includes(name as ToolGroupName)) groups.push(name as ToolGroupName);
    } else {
      unknownGroups.push(name);
    }
  }

  return { base, groups, unknownGroups };
}

/** Tool names a `lite` profile lists: the everyday set plus the named groups. */
export function listedToolsFor(groups: readonly ToolGroupName[]): Set<string> {
  const listed = new Set<string>(LITE_TOOLS);
  for (const group of groups) {
    for (const tool of TOOL_GROUPS[group]) listed.add(tool);
  }
  return listed;
}

/**
 * Hide every registered tool outside {@link listedToolsFor}. Returns the names
 * that were hidden, so registration can report the count it actually achieved
 * rather than the one it intended.
 */
export function hideUnlistedTools(
  handles: ReadonlyMap<string, ToolHandle>,
  groups: readonly ToolGroupName[]
): string[] {
  const listed = listedToolsFor(groups);
  const hidden: string[] = [];

  for (const [name, handle] of handles) {
    if (listed.has(name)) continue;
    try {
      handle.disable();
      hidden.push(name);
    } catch (error) {
      console.error(`не удалось скрыть инструмент ${name}:`, error);
    }
  }

  return hidden;
}
