#!/usr/bin/env node
/**
 * Drift check: command.json ↔ server/src/tools ↔ known aliases.
 * Run from repo root: node scripts/check-tool-registry.mjs
 * Or: npm run check:tool-registry (from server/)
 *
 * Exit 0 = OK, 1 = drift / policy violation.
 */
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, "..");

/** MCP tool name → Revit commandName */
const ALIASES = {
  color_elements: "color_splash",
  tag_all_rooms: "tag_rooms",
  tag_all_walls: "tag_walls",
};

/** Revit commands that must NOT have a public 1:1 MCP tool (internal building blocks). */
const INTERNAL_COMMANDS = new Set(["export_egress_graph"]);

/**
 * Tools that overlap enough for the model to reach for the wrong one (Д8). They are
 * kept apart by their descriptions, not by the schema, so each must name its
 * sibling and say when to prefer it — otherwise a wrong first pick costs a whole
 * extra round trip. Editing a description without keeping the pointer fails here.
 */
const TOOL_TWINS = [
  ["create_text_note", "create_text_notes"],
  ["tag_all_walls", "tag_elements"],
  ["tag_all_rooms", "tag_elements"],
  ["create_dimensions", "dimension_room_walls"],
  ["create_dimensions", "dimension_grids"],
  ["dimension_room_walls", "dimension_grids"],
  ["set_element_parameter", "set_elements_parameters"],
];

/**
 * MCP tools that intentionally have no matching command.json entry
 * (server-only orchestration / norm library / aliases already mapped).
 */
const SERVER_ONLY_TOOLS = new Set([
  "extract_norm_rules_from_pdf",
  "query_norm_rules",
  "save_norm_rule",
  "run_norm_audit",
  "annotate_norm_findings",
  "check_accessibility",
  "check_door_width",
  "check_evacuation_distance",
  "check_room_norms",
  "check_tambour_size",
  "check_vertical_circulation",
  "check_window_openings",
  "fill_title_block",
  "number_rooms",
  "trace_walls_from_cad",
  "trace_openings_from_cad",
  "trace_columns_from_cad",
  "color_elements",
  "tag_all_rooms",
  "tag_all_walls",
  // legacy full-profile only
  "store_room_data",
  "store_project_data",
  "query_stored_data",
]);

/** Must never appear in PRIORITY_TOOL_FILES without a real tool module. */
const FORBIDDEN_WITHOUT_FILE = ["highlight_room_tags"];

/** Empty / removed stubs — must not exist. */
const FORBIDDEN_STUBS = ["modify_element", "search_modules", "use_module"];

const errors = [];
const warnings = [];

function readJson(rel) {
  return JSON.parse(fs.readFileSync(path.join(root, rel), "utf8"));
}

function listToolBases() {
  const dir = path.join(root, "server", "src", "tools");
  return fs
    .readdirSync(dir)
    .filter(
      (f) =>
        (f.endsWith(".ts") || f.endsWith(".js")) &&
        !f.includes(".test.") &&
        f !== "register.ts" &&
        f !== "register.js" &&
        f !== "index.ts" &&
        f !== "index.js"
    )
    .map((f) => f.replace(/\.(ts|js)$/, ""));
}

function commandNamesFromManifest() {
  const manifest = readJson("command.json");
  const list = manifest.commands || manifest;
  if (!Array.isArray(list)) {
    errors.push("command.json: expected .commands array");
    return [];
  }
  return list.map((c) => c.commandName).filter(Boolean);
}

function commandsetCommandNames() {
  const names = new Set();
  const commandsRoot = path.join(root, "commandset", "Commands");
  if (!fs.existsSync(commandsRoot)) {
    warnings.push("commandset/Commands not found — skip C# CommandName scan");
    return names;
  }

  function walk(dir) {
    for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, ent.name);
      if (ent.isDirectory()) walk(p);
      else if (ent.name.endsWith(".cs")) {
        const text = fs.readFileSync(p, "utf8");
        for (const m of text.matchAll(/CommandName\s*=>\s*"([^"]+)"/g)) {
          names.add(m[1]);
        }
      }
    }
  }
  walk(commandsRoot);
  return names;
}

function checkRegisterPriority() {
  const registerPath = path.join(root, "server", "src", "tools", "register.ts");
  const text = fs.readFileSync(registerPath, "utf8");
  for (const name of FORBIDDEN_WITHOUT_FILE) {
    if (text.includes(`"${name}"`)) {
      const toolFile = path.join(
        root,
        "server",
        "src",
        "tools",
        `${name}.ts`
      );
      if (!fs.existsSync(toolFile)) {
        errors.push(
          `register.ts references "${name}" but server/src/tools/${name}.ts is missing`
        );
      }
    }
  }
}

/**
 * The description a tool registers with — everything between the tool name and the
 * schema object in its `server.tool(...)` call.
 */
function readToolDescription(base) {
  const p = path.join(root, "server", "src", "tools", `${base}.ts`);
  if (!fs.existsSync(p)) return null;
  const text = fs.readFileSync(p, "utf8");
  const start = text.indexOf(`"${base}"`);
  if (start < 0) return null;
  const end = text.indexOf("\n    {", start);
  return text.slice(start, end < 0 ? text.length : end);
}

function checkTwinCrossReferences(toolSet) {
  for (const [a, b] of TOOL_TWINS) {
    for (const [tool, sibling] of [
      [a, b],
      [b, a],
    ]) {
      if (!toolSet.has(tool)) {
        errors.push(`TOOL_TWINS names "${tool}", but server/src/tools/${tool}.ts is missing`);
        continue;
      }
      const description = readToolDescription(tool);
      if (description === null) {
        errors.push(`Could not read the registered description of "${tool}"`);
        continue;
      }
      if (!description.includes(sibling)) {
        errors.push(
          `"${tool}" does not point at its twin "${sibling}" in its description. ` +
            `They overlap, so the model needs to be told which one to prefer ` +
            `(see TOOL_TWINS in check-tool-registry.mjs)`
        );
      }
    }
  }
}

function main() {
  const tools = listToolBases();
  const toolSet = new Set(tools);
  const manifestNames = commandNamesFromManifest();
  const manifestSet = new Set(manifestNames);
  const csharpNames = commandsetCommandNames();

  // --- empty / forbidden stubs ---
  for (const stub of FORBIDDEN_STUBS) {
    const p = path.join(root, "server", "src", "tools", `${stub}.ts`);
    if (fs.existsSync(p)) {
      errors.push(`Forbidden stub still present: server/src/tools/${stub}.ts (delete it)`);
    }
  }

  for (const t of tools) {
    const p = path.join(root, "server", "src", "tools", `${t}.ts`);
    if (fs.existsSync(p) && fs.statSync(p).size === 0) {
      errors.push(`Empty tool file: server/src/tools/${t}.ts`);
    }
  }

  checkRegisterPriority();
  checkTwinCrossReferences(toolSet);

  // --- alias map integrity ---
  for (const [mcp, revit] of Object.entries(ALIASES)) {
    if (mcp === revit) continue;
    if (!toolSet.has(mcp)) {
      errors.push(`Alias MCP tool missing: ${mcp} → ${revit}`);
    }
    if (!manifestSet.has(revit)) {
      errors.push(`Alias target missing from command.json: ${mcp} → ${revit}`);
    }
  }

  // --- command.json entries must exist in C# (except plugin:builtin) ---
  const manifest = readJson("command.json");
  for (const c of manifest.commands || []) {
    const name = c.commandName;
    const asm = String(c.assemblyPath || "");
    if (asm.includes("plugin:builtin")) continue;
    if (csharpNames.size && !csharpNames.has(name)) {
      errors.push(
        `command.json "${name}" has no CommandName in commandset/Commands`
      );
    }
  }

  // --- every non-server-only tool maps to a command.json name ---
  for (const t of tools) {
    if (SERVER_ONLY_TOOLS.has(t)) continue;
    const revitName = ALIASES[t] || t;
    if (!manifestSet.has(revitName)) {
      errors.push(
        `MCP tool "${t}" has no command.json entry (expected "${revitName}"). ` +
          `If intentional, add to SERVER_ONLY_TOOLS in check-tool-registry.mjs and docs/tool-registry.md`
      );
    }
  }

  // --- internal commands should not have a same-named MCP tool ---
  for (const name of INTERNAL_COMMANDS) {
    if (toolSet.has(name)) {
      errors.push(
        `Internal command "${name}" should not have a public MCP tool file`
      );
    }
    if (!manifestSet.has(name)) {
      warnings.push(
        `Internal command "${name}" missing from command.json (callers may break)`
      );
    }
  }

  // --- report ---
  console.log(`Tools: ${tools.length}`);
  console.log(`command.json commands: ${manifestNames.length}`);
  console.log(`commandset CommandName: ${csharpNames.size}`);
  console.log(`Aliases: ${Object.keys(ALIASES).filter((k) => ALIASES[k] !== k).join(", ")}`);
  console.log(`Twin pairs cross-referenced: ${TOOL_TWINS.length}`);

  if (warnings.length) {
    console.log("\nWarnings:");
    for (const w of warnings) console.log(`  - ${w}`);
  }
  if (errors.length) {
    console.log("\nErrors:");
    for (const e of errors) console.log(`  - ${e}`);
    process.exit(1);
  }
  console.log("\nOK — tool registry drift check passed.");
}

main();
