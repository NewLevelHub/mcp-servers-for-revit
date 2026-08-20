import path from "node:path";
import type { ModelSelection } from "@cursor/sdk";

export type BridgeConfig = {
  port: number;
  apiKey: string;
  /** Shared secret the Revit panel sends as x-bridge-token; empty disables the check. */
  token: string;
  model: ModelSelection;
  modelLabel: string;
  /**
   * Model for everyday turns when the user left the picker on "Авто" (REV-157).
   * The router itself is a hop the architect waits through, and most chat turns
   * are a question or a couple of walls — they do not need it.
   */
  fastModel: ModelSelection;
  /**
   * True when the configured model is the auto router, i.e. we are free to pick
   * per turn. A user who pinned a model gets that model, always.
   */
  routePerTurn: boolean;
  /** Tool profile handed to the Revit MCP server. `default` lists all 92; `lite` ~22. */
  mcpToolProfile: string;
  rulesCwd: string;
  mcpNode: string;
  mcpServerJs: string;
  mcpServerCwd: string;
  /** Where the local agent store lives; kept short, see loadConfig. */
  storeDir: string;
};

/**
 * Cursor's auto-router. Verified against Cursor.models.list(): the id is
 * "default" (displayName "Auto", alias "auto") and it takes no parameters —
 * there is no "auto-smart" model and no optimize_for knob.
 */
export const AUTO_MODEL_ID = "default";

/** Default for the fast lane; override with ASSISTANT_CURSOR_FAST_MODEL. */
export const DEFAULT_FAST_MODEL_ID = "composer-2.5";

/** Legacy ids written by earlier builds of the settings page. */
const LEGACY_AUTO_IDS = new Set([
  "auto",
  "auto-smart",
  "auto-smart:balanced",
  "auto-smart:intelligence",
]);

const MODEL_LABELS: Record<string, string> = {
  [AUTO_MODEL_ID]: "Авто",
  "composer-2.5": "Composer 2.5",
  "claude-sonnet-5": "Claude Sonnet 5",
  "claude-opus-5": "Claude Opus 5",
  "gpt-5.6-sol": "GPT-5.6",
  "gemini-3.1-pro": "Gemini 3.1 Pro",
};

/** Parse a stored model id; anything auto-flavoured collapses to the router. */
export function parseModelSelection(raw: string): ModelSelection {
  const value = (raw || AUTO_MODEL_ID).trim();
  if (!value || LEGACY_AUTO_IDS.has(value)) {
    return { id: AUTO_MODEL_ID };
  }
  return { id: value };
}

export function modelLabel(selection: ModelSelection): string {
  return MODEL_LABELS[selection.id] ?? selection.id;
}

export function loadConfig(): BridgeConfig {
  const apiKey = (process.env.CURSOR_API_KEY ?? process.env.ASSISTANT_CURSOR_API_KEY ?? "").trim();
  if (!apiKey) {
    throw new Error("CURSOR_API_KEY is required for assistant-bridge");
  }

  const port = Number(process.env.ASSISTANT_BRIDGE_PORT ?? "8790");
  const modelRaw = (process.env.ASSISTANT_CURSOR_MODEL ?? AUTO_MODEL_ID).trim();
  const model = parseModelSelection(modelRaw);
  const rulesCwd = (process.env.CURSOR_RULES_CWD ?? process.cwd()).trim();
  const mcpNode = (process.env.REVIT_MCP_NODE ?? "node").trim();
  const mcpServerJs = (process.env.REVIT_MCP_SERVER_JS ?? "").trim();
  const mcpServerCwd = (process.env.REVIT_MCP_SERVER_CWD ?? "").trim();

  if (!mcpServerJs) {
    throw new Error("REVIT_MCP_SERVER_JS path to server/build/index.js is required");
  }

  const fastModel = parseModelSelection(
    (process.env.ASSISTANT_CURSOR_FAST_MODEL ?? DEFAULT_FAST_MODEL_ID).trim()
  );

  return {
    port: Number.isFinite(port) && port > 0 ? port : 8790,
    apiKey,
    token: (process.env.ASSISTANT_BRIDGE_TOKEN ?? "").trim(),
    model,
    modelLabel: modelLabel(model),
    fastModel,
    routePerTurn: model.id === AUTO_MODEL_ID,
    // Full catalog unless someone asks for less. `lite` was the default for one
    // release and it cost more than the tokens it saved: every group it hid, the
    // model reported as a missing feature rather than a hidden tool — марки
    // помещений, цветовые схемы, проверки по СП. Set `MCP_TOOL_PROFILE=lite` to
    // trade that back for speed.
    mcpToolProfile: (process.env.MCP_TOOL_PROFILE ?? "default").trim().toLowerCase(),
    rulesCwd,
    mcpNode,
    mcpServerJs,
    mcpServerCwd: mcpServerCwd || rulesCwd,
    storeDir: resolveStoreDir(),
  };
}

/**
 * The SDK's default agent store nests per-cwd and per-agent SQLite files, which
 * blows past Windows MAX_PATH when the workspace lives under
 * %AppData%\Autodesk\Revit\Addins\... — every turn then dies with
 * "unable to open database file". Keep the store on a short, flat path instead.
 */
function resolveStoreDir(): string {
  const override = (process.env.ASSISTANT_AGENT_STORE ?? "").trim();
  if (override) return override;

  const base =
    (process.env.LOCALAPPDATA ?? "").trim() ||
    (process.env.HOME ?? "").trim() ||
    process.cwd();

  return path.join(base, "RevitAI", "agent-store");
}