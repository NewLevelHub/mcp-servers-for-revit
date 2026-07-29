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
| `norms` | Only `extract_norm_rules_from_pdf`, `query_norm_rules`, `save_norm_rule` |
| `full` | Everything including legacy SQLite helpers |

### Legacy / full-only (keep files, not in default)

| MCP tool | Notes |
|----------|-------|
| `store_room_data` | Local SQLite room metadata |
| `store_project_data` | Local SQLite project metadata |
| `query_stored_data` | Query local SQLite store |

Listed in `DEFAULT_DENYLIST` in `server/src/tools/register.ts`. Use only with `MCP_TOOL_PROFILE=full`.

Empty stub files (`modify_element`, `search_modules`, `use_module`) were removed — do not reintroduce without an implementation.

## Name aliases (MCP → Revit command)

These are **intentional**. MCP keeps a stable AI-facing name; Revit keeps the historical `CommandName`.

| MCP tool | Revit `commandName` |
|----------|---------------------|
| `color_elements` | `color_splash` |
| `tag_all_rooms` | `tag_rooms` |
| `tag_all_walls` | `tag_walls` |

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
| `export_egress_graph` | `internal` | Used by `check_evacuation_distance`, `number_rooms`; no `export_egress_graph.ts` |
| `run_norm_audit` | `server-only` (+ thin plugin orchestrator for in-Revit chat) | Full audit in `server/src/normatives/normAudit/` |
| `annotate_norm_findings` | `server-only` (+ plugin helper for in-Revit chat) | Composes `create_text_notes` / leaders |
| `extract_norm_rules_from_pdf` / `query_norm_rules` / `save_norm_rule` | `server-only` | SQLite / PDF; no Revit call |
| `fill_title_block` / `number_rooms` | `server-only` | Orchestrate existing Revit commands |
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

## Phase-2 ownership notes (cleanup)

- `operate_element` payload shape (`data` wrapper, `categoryNames` string→array): **commandset** `OperateElementParameterNormalizer`.
- In-Revit assistant may still map audit `findings` / `doorElementIds` → `elementIds` in `CreateElementArgsNormalizer` (LLM-only convenience).
- In-Revit `run_norm_audit`: thin `NormAuditOrchestrator` (4 checkers). Full audit: server `run_norm_audit`.

## Related

- Agent rules: [AGENTS.md](../AGENTS.md), [.cursor/rules/revit-mcp.mdc](../.cursor/rules/revit-mcp.mdc)
- Drift script: [scripts/check-tool-registry.mjs](../scripts/check-tool-registry.mjs)
