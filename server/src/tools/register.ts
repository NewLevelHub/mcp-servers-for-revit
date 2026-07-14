import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

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
  "check_fire_doors",
  "check_room_depth",
  "check_min_dimensions",
  "apply_norm_result",
  "highlight_room_tags",
] as const;

/** Only these tools when MCP_TOOL_PROFILE=norms. */
const NORMS_ALLOWLIST = new Set([
  "extract_norm_rules_from_pdf",
  "query_norm_rules",
  "save_norm_rule",
]);

/**
 * Skipped in the default profile — legacy DB helpers and stubs.
 * Set MCP_TOOL_PROFILE=full to register everything with a register* fn.
 */
const DEFAULT_DENYLIST = new Set([
  "store_room_data",
  "store_project_data",
  "query_stored_data",
  "modify_element",
  "search_modules",
  "use_module",
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
  return !DEFAULT_DENYLIST.has(base);
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

  let registeredToolCount = 0;

  for (const file of files) {
    const base = toolBaseName(file);
    if (!shouldRegisterTool(base, profile)) {
      console.error(`跳过工具 (profile=${profile}): ${file}`);
      continue;
    }

    try {
      if (await registerToolFile(server, file)) {
        registeredToolCount += 1;
      }
    } catch (error) {
      console.error(`注册工具 ${file} 时出错:`, error);
    }
  }

  console.error(
    `Tool registration done: ${registeredToolCount} tools (profile=${profile})`
  );
  return registeredToolCount;
}
