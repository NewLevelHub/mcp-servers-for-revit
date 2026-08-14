# Tool registry — MCP ↔ Revit command contract

Single source of truth for **names and ownership**. When adding a capability, update this doc (and keep `scripts/check-tool-registry.mjs` green).

## Layers

| Layer | Path | Role |
|-------|------|------|
| MCP tools | `server/src/tools/*.ts` | Zod schema + AI-facing name; often wraps / orchestrates |
| Revit commands | root `command.json` + `commandset/Commands/` | JSON-RPC method executed in Revit |
| In-Revit assistant | `plugin/Core/Assistant/ToolCatalog.cs` | Curated subset + RU labels for the dockable chat |

```
Cursor / MCP client
  → server tool (MCP name)
    → sendCommand(Revit command name)   // may differ (aliases)
      → plugin CommandExecutor
        → commandset *Command / *EventHandler
```

Some MCP tools never call Revit (norm library). Some Revit commands are **internal** (no public MCP tool).

## Profiles (`MCP_TOOL_PROFILE`)

| Profile | Behavior |
|---------|----------|
| `default` (unset) | All tools with a `register*` export **except** `DEFAULT_DENYLIST` |
| `lite` | Same set, but only `LITE_ALLOWLIST` (~20 tools) is **listed**; the rest are registered and hidden |
| `norms` | Only `extract_norm_rules_from_pdf`, `query_norm_rules`, `save_norm_rule` |
| `full` | Everything including legacy SQLite helpers |

### `lite` and per-turn profile switching (REV-157)

The full catalog serialises to ~188 KB of JSON schema — about 50k tokens the
model reads before writing its first character, on **every** turn. That is the
main reason a one-line question took ~12 s in the in-Revit chat. `lite` lists 20
everyday tools (28 KB, ~8k tokens) and hides the other 70.

The assistant-bridge picks the profile **per turn** and passes it through
`agent.send(message, { mcpServers })`: `lite` for questions and everyday edits,
`default` when the request looks like real work (DWG, layout, norms, schedules,
sheets, images, long prompts — `isHeavyRequest` in `agent-session.ts`). The
conversation and its history stay on the same agent across the switch.

Verified against Cursor SDK 1.0.24 on a live model:

- Per-send `mcpServers` **works** — a turn asking for `get_cad_link_geometry`
  reached it in a session whose previous turns ran on `lite`.
- Runtime unhiding does **not** work. `RegisteredTool.enable()` emits
  `notifications/tools/list_changed`, but Cursor snapshots the MCP catalog when
  the agent is created: the newly enabled tool fails with
  `Tool mcp-server-for-revit-local-<name> was not found`, on that run **and** on
  the next send in the same session. Do not reintroduce an in-conversation
  escalation tool without re-testing this.

Cursor IDE and any other client keep the `default` profile unless they set the
env var.

### Legacy / full-only (keep files, not in default)

| MCP tool | Notes |
|----------|-------|
| `store_room_data` | Local SQLite room metadata |
| `store_project_data` | Local SQLite project metadata |
| `query_stored_data` | Query local SQLite store |

Listed in `DEFAULT_DENYLIST` in `server/src/tools/register.ts`. Use only with `MCP_TOOL_PROFILE=full`.

Empty stub files (`modify_element`, `search_modules`, `use_module`) were removed — do not reintroduce without an implementation.

## Assistant tool profiles (in-Revit chat, REV-112)

Separate from `MCP_TOOL_PROFILE` (server env). The dockable assistant filters
`plugin/Core/Assistant/ToolCatalog.cs` so the model sees **≤ 30** tools per
request instead of the full ~70.

| Layer | Contents |
|-------|----------|
| **core** (always) | `get_current_view_info`, `get_current_view_elements`, `get_selected_elements`, `get_available_family_types`, `get_element_parameters`, `set_element_parameter`, `export_room_data`, `operate_element`, `delete_element`, `query_norm_rules` |
| **modeling** | CAD tracing (`get_cad_link_geometry`, `trace_*_from_cad`), create_* elements, rooms/levels/stairs/railings/openings, **`ensure_wall_type`**, `ensure_opening_type` |
| **annotation** | grids, dimensions, `tag_rooms` / `tag_walls`, text notes, detail lines/views/regions, **`create_node_detail`**, `place_detail_component`, `load_family`, `get_document_styles`, `color_splash` |
| **schedules** | door/window/floor schedules, floor explication, TEP (`render_tep_table` / `export_tep_data`), schedule configure/validate |
| **sheets** | `create_sheet`, `place_view_on_sheet`, `auto_layout_sheet`, `fit_schedule_to_sheet` |
| **norms** | `run_norm_audit`, `check_*`, filled regions (`create_filled_regions` — room/plan only), annotate findings, geometry helpers (no `export_egress_graph`) |
| **data** | other `export_*`, materials, `analyze_model_statistics`, `ai_element_filter`, `batch_execute`, `send_code_to_revit` |

**How profiles are chosen**

1. Scenario chip → `ScenarioPreset.Profiles` (exact).
2. Free text → `IntentRouter` heuristic (keywords); optional cheap LLM call without tools if ambiguous.
3. **Escalation:** if the model calls a tool outside the active set, the host returns `tool_not_in_profile` with `availableInProfiles`, merges those profiles, and expands the catalog on the next round (no hard fail).

API: `ToolCatalog.GetOpenAiTools(profiles)`, `IntentRouter.ResolveHeuristic` / `ResolveAsync`.

## Name aliases (MCP → Revit command)

These are **intentional**. MCP / Cursor may send the stable AI-facing name; Revit keeps the historical `CommandName`. The **in-Revit assistant** catalog lists only the canonical name; `ToolCatalog.ResolveToolAlias` maps legacy names before execute (REV-116).

| MCP / alias | Canonical Revit `commandName` (assistant catalog) |
|-------------|-----------------------------------------------------|
| `color_elements` | `color_splash` |
| `tag_all_rooms` | `tag_rooms` |
| `tag_all_walls` | `tag_walls` |

`fill_title_block` and `number_rooms` are **server-only** (Cursor MCP). They are not in the assistant catalog; calling them returns a clear Russian soft-error.

## Ownership legend

| Tag | Meaning |
|-----|---------|
| `commandset` | 1:1 MCP tool ↔ Revit command (same name, unless aliased) |
| `server-only` | Logic / orchestration in TypeScript; may call one or more Revit commands |
| `plugin-builtin` | Handled inside the Revit add-in (not a commandset class), e.g. `batch_execute` |
| `internal` | In `command.json` / C#, **no** public MCP tool — building block for other tools |

## Aliases & special cases

| Name | Kind | Notes |
|------|------|-------|
| `batch_execute` | `plugin-builtin` | `assemblyPath: plugin:builtin` in `command.json` |
| `export_egress_graph` | `internal` | Used by `check_evacuation_distance`, `number_rooms`; **not** in in-Revit assistant catalog (REV-116) |
| `run_norm_audit` | `server-only` (+ thin plugin orchestrator for in-Revit chat) | Full audit in `server/src/normatives/normAudit/` |
| `annotate_norm_findings` | `server-only` (+ plugin helper for in-Revit chat) | Composes `create_text_notes` / leaders |
| `extract_norm_rules_from_pdf` / `query_norm_rules` / `save_norm_rule` | `server-only` | SQLite / PDF; no Revit call |
| `fill_title_block` / `number_rooms` | `server-only` | Cursor MCP only; not in assistant `Definitions` — soft-error if invented |
| `trace_walls_from_cad` | `server-only` | Orchestrates `get_cad_link_geometry` + geometry merge + `create_line_based_element` + verify (REV-140). REV-152/153: `openingGapMm` joins a run across a gap **only where the CAD shows a door or window**, so walls stay continuous and Revit cuts the openings instead of every door getting a stub host; verify samples along the axis instead of judging it by its midpoint |
| `trace_openings_from_cad` | `server-only` | Orchestrates CAD opening detection + host match + `create_point_based_element` + verify (REV-147/148/149); category door\|window\|both. REV-149: doors come from DWG swing arcs (hinge = arc centre → exact centre, width, swing side and hand); `strictLocation` defaults on; verify reads the placed elements back instead of comparing the plan with itself. REV-152: the placed door's own plan swing arc is measured against the DWG arc — `swingMismatchCount` / `swingIssues`. REV-153: `exactTypes` calls `ensure_opening_type` so an opening is built at its traced width, not the nearest stock size |
| `ensure_opening_type` | `command.json` | Returns a door/window `FamilySymbol` of a requested width/height, duplicating the source type and setting its size when the project has nothing that close (REV-153) |
| `ensure_wall_type` | `command.json` | Duplicate a wall type and set core thickness (REV-154); also in assistant modeling profile |
| `create_node_detail` | `commandset` | Drafting node from wall/floor CompoundStructure (junction/single); hatches, dimensions, labels |
| `create_detail_regions` | `commandset` | Hatch arbitrary contours on drafting/detail/section/plan (`MCP-DR`); not room-based `create_filled_regions` |
| `load_family` | `commandset` | `doc.LoadFamily` for `.rfa` paths on the Revit machine; returns loaded types for `place_detail_component` |
| `create_detail_view` | `commandset` | Modes: `callout`, `drafting`, **`section`** (live cut; Fine draws compound layers) |
| `create_detail_lines` | `commandset` | Polylines + arcs + `lineStyleName` (OST_Lines subcategories) |
| `get_document_styles` | `commandset` | Also returns `lineStyles`, `filledRegionTypes`, `fillPatterns` (not only dimensions/grids/text) |
| `trace_columns_from_cad` | `server-only` | Orchestrates `get_cad_link_geometry` + column symbol grouping + `create_point_based_element` with rotation (REV-149); rectangular and round columns. Columns must **not** go through `trace_walls_from_cad` — they come out as four stubs |
| `check_door_width`, `check_tambour_size`, `check_room_norms`, `check_window_openings`, `check_vertical_circulation`, `check_accessibility`, `check_evacuation_distance` | `server-only` (or hybrid) | Often compose geometry/export commands + norm library; may not have a matching `check_*` in `command.json` |
| `highlight_room_tags` | **removed / not implemented** | Do not advertise; do not add to `PRIORITY_TOOL_FILES` without a tool file |

## Default 1:1 map

Unless listed under aliases or special cases, MCP tool name **equals** Revit `commandName` and lives in `commandset`.

Examples: `say_hello`, `get_current_view_info`, `create_line_based_element`, `operate_element`, `create_filled_regions`, `check_evacuation_width`, `check_room_depth`, `check_min_dimensions`, `check_fire_doors`, …

Full machine-checkable lists: run `npm run check:tool-registry` from `server/` (script lives at repo root `scripts/check-tool-registry.mjs`).

## Checklist: adding a tool

1. Implement `commandset` Command + EventHandler (if Revit work is needed).
2. Register in root `command.json`.
3. Add `server/src/tools/<mcp_name>.ts` with `register*` export; if MCP name ≠ command name, document alias here and in the known-alias map in the check script.
4. Optionally expose in `plugin/Core/Assistant/ToolCatalog.cs` for in-Revit chat.
5. Run `npm run check:tool-registry` (from `server/`) and update this doc if ownership/alias changed.

## Related

- Agent rules: [AGENTS.md](../AGENTS.md), [.cursor/rules/revit-mcp.mdc](../.cursor/rules/revit-mcp.mdc)
- Drift script: [scripts/check-tool-registry.mjs](../scripts/check-tool-registry.mjs)
