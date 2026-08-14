import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { wrapToolHandler } from "../utils/toolOutcome.js";

/**
 * Register norms + check_* first (REV-46).
 * Note: Cursor may spawn MCP with its bundled Node (ABI 127). mcp.json must
 * point `command` at a system Node that matches better-sqlite3's build, or
 * extract/query/save fail to register (native module load error).
 */
const PRIORITY_TOOL_FILES = [
  "extract_norm_rules_from_pdf",
  "query_norm_rules",
  "save_norm_rule",
  "check_evacuation_width",
  "check_evacuation_distance",
  "check_fire_doors",
  "check_room_depth",
  "check_min_dimensions",
  "check_door_width",
  "check_tambour_size",
  "check_accessibility",
  "check_room_norms",
  "check_window_openings",
  "check_vertical_circulation",
  "run_norm_audit",
  "apply_norm_result",
] as const;

/** Only these tools when MCP_TOOL_PROFILE=norms. */
const NORMS_ALLOWLIST = new Set([
  "extract_norm_rules_from_pdf",
  "query_norm_rules",
  "save_norm_rule",
]);

/**
 * Skipped in the default profile — legacy local SQLite helpers.
 * Available only with MCP_TOOL_PROFILE=full. See docs/tool-registry.md.
 * Empty stubs (modify_element / search_modules / use_module) were removed.
 */
const DEFAULT_DENYLIST = new Set([
  "store_room_data",
  "store_project_data",
  "query_stored_data",
]);

/**
 * Tools that stay listed in the `lite` profile (REV-157).
 *
 * The full catalog serialises to ~188 KB of JSON schema — roughly 50k tokens the
 * model reads before writing its first character, on every single turn. An
 * architect asking a one-line question waits through all of it. `lite` lists the
 * everyday set (~29 KB); the assistant-bridge asks for the `default` profile on
 * the turns that need more. Registration is unchanged — the rest is registered
 * and then hidden, so a profile switch costs no extra wiring.
 */
const LITE_ALLOWLIST = new Set([
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
  "operate_element",
  "delete_element",
]);

function toolBaseName(file: string): string {
  return file.replace(/\.(ts|js)$/, "");
}

function isToolModuleFile(file: string): boolean {
  if (!file.endsWith(".ts") && !file.endsWith(".js")) return false;
  if (file === "index.ts" || file === "index.js") return false;
  if (file === "register.ts" || file === "register.js") return false;
  if (file.includes(".test.")) return false;
  return true;
}

function sortToolFiles(files: string[]): string[] {
  const priorityIndex = new Map(
    PRIORITY_TOOL_FILES.map((name, index) => [name, index])
  );

  return [...files].sort((a, b) => {
    const aBase = toolBaseName(a);
    const bBase = toolBaseName(b);
    const aPri = priorityIndex.get(
      aBase as (typeof PRIORITY_TOOL_FILES)[number]
    );
    const bPri = priorityIndex.get(
      bBase as (typeof PRIORITY_TOOL_FILES)[number]
    );
    if (aPri !== undefined && bPri !== undefined) return aPri - bPri;
    if (aPri !== undefined) return -1;
    if (bPri !== undefined) return 1;
    return aBase.localeCompare(bBase);
  });
}

function shouldRegisterTool(base: string, profile: string): boolean {
  if (profile === "full") return true;
  if (profile === "norms") return NORMS_ALLOWLIST.has(base);
  // `lite` shares the default set; the trimming happens after registration.
  return !DEFAULT_DENYLIST.has(base);
}

type ToolHandle = { enable(): void; disable(): void };

/**
 * Every `server.tool` overload ends with the handler, so the last function
 * argument is the one to wrap regardless of which overload a module used.
 */
function wrapHandlerArgument(name: string, args: unknown[]): unknown[] {
  for (let i = args.length - 1; i >= 0; i--) {
    if (typeof args[i] === "function") {
      const copy = [...args];
      copy[i] = wrapToolHandler(name, args[i] as (...a: never[]) => unknown);
      return copy;
    }
  }
  return args;
}

/**
 * Hand the tool modules a server that (a) routes every handler through
 * {@link wrapToolHandler} so a refusal from Revit reaches the model as an
 * error instead of a success-shaped payload, and (b) records the tool handles
 * so the lite profile can hide the non-core ones afterwards.
 *
 * The modules keep calling `server.tool(...)` unchanged — doing this here beats
 * editing the same three lines into 94 handlers.
 */
function captureTools(server: McpServer, sink: Map<string, ToolHandle>): McpServer {
  const wrap =
    (method: "tool" | "registerTool") =>
    (...args: unknown[]) => {
      const name = typeof args[0] === "string" ? args[0] : undefined;
      const effectiveArgs = name ? wrapHandlerArgument(name, args) : args;
      const handle = (server as unknown as Record<string, (...a: unknown[]) => unknown>)[
        method
      ](...effectiveArgs);
      if (name && handle) sink.set(name, handle as ToolHandle);
      return handle;
    };

  return new Proxy(server, {
    get(target, prop) {
      if (prop === "tool" || prop === "registerTool") return wrap(prop);
      // Read through the target, not the proxy: McpServer methods rely on their
      // own internals and must not be re-entered through this trap.
      const value = Reflect.get(target, prop, target);
      return typeof value === "function" ? value.bind(target) : value;
    },
  }) as McpServer;
}

/** Hide everything outside the lite set. Returns how many tools were hidden. */
function lockNonLiteTools(handles: Map<string, ToolHandle>): number {
  let locked = 0;
  for (const [name, handle] of handles) {
    if (LITE_ALLOWLIST.has(name)) continue;
    try {
      handle.disable();
      locked += 1;
    } catch (error) {
      console.error(`не удалось скрыть инструмент ${name}:`, error);
    }
  }
  return locked;
}

async function registerToolFile(
  server: McpServer,
  file: string
): Promise<boolean> {
  const importPath = `./${file.replace(/\.(ts|js)$/, ".js")}`;
  const module = await import(importPath);
  const registerFunctionName = Object.keys(module).find(
    (key) => key.startsWith("register") && typeof module[key] === "function"
  );

  if (!registerFunctionName) {
    console.warn(`警告: 在文件 ${file} 中未找到注册函数`);
    return false;
  }

  module[registerFunctionName](server);
  console.error(`已注册工具: ${file}`);
  return true;
}

export async function registerTools(server: McpServer): Promise<number> {
  const __filename = fileURLToPath(import.meta.url);
  const __dirname = path.dirname(__filename);
  const profile = (process.env.MCP_TOOL_PROFILE ?? "default").toLowerCase();

  console.error(
    `Node ${process.version} (ABI ${process.versions.modules}), profile=${profile}`
  );

  const files = sortToolFiles(
    fs.readdirSync(__dirname).filter(isToolModuleFile)
  );

  const handles = new Map<string, ToolHandle>();
  const lite = profile === "lite";
  // Always proxied: the handler wrapping matters in every profile, and the
  // captured handles are only consulted for `lite`.
  const target = captureTools(server, handles);

  let registeredToolCount = 0;

  for (const file of files) {
    const base = toolBaseName(file);
    if (!shouldRegisterTool(base, profile)) {
      console.error(`跳过工具 (profile=${profile}): ${file}`);
      continue;
    }

    try {
      if (await registerToolFile(target, file)) {
        registeredToolCount += 1;
      }
    } catch (error) {
      console.error(`注册工具 ${file} 时出错:`, error);
    }
  }

  if (lite) {
    const locked = lockNonLiteTools(handles);
    console.error(`lite profile: ${handles.size - locked} tools listed, ${locked} hidden`);
  }

  console.error(
    `Tool registration done: ${registeredToolCount} tools (profile=${profile})`
  );
  return registeredToolCount;
}
