/**
 * Revit answers every command with an AIResult (`{ Success, Message, Response }`),
 * and tool modules used to JSON.stringify that straight into a *successful* MCP
 * result. A refusal therefore reached the model looking exactly like a success:
 * `isError` was never set, and the reason sat in prose three levels deep. The
 * model could not tell "done" from "could not do it", so it re-tried the same
 * call with guessed arguments instead of reporting the problem.
 *
 * This normalises every tool result in one place (see register.ts) rather than
 * in 94 hand-written handlers.
 */

export type ToolContent = {
  type: string;
  text?: string;
  [key: string]: unknown;
};

export type ToolResult = {
  content?: ToolContent[];
  isError?: boolean;
  [key: string]: unknown;
};

/** Refusal flags, in the casings actually produced by the C# and CAD paths. */
const FAILURE_FLAGS = ["Success", "success", "ok"] as const;

/**
 * A refusal the model must not read as success. Deliberately narrow: only an
 * explicit `false` on a known flag counts. A norm check reporting a violation,
 * an empty list, or a body without these flags is a successful call.
 */
function findRefusal(value: unknown): { reason?: string } | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const bag = value as Record<string, unknown>;

  const refused = FAILURE_FLAGS.some((flag) => bag[flag] === false);
  if (!refused) {
    // Wrapper shapes such as { annotatedCount, result: <AIResult> } hide the
    // real verdict one level down.
    const nested = bag.result ?? bag.response ?? bag.Response;
    return nested && nested !== value ? findRefusal(nested) : null;
  }

  const reason = [bag.Message, bag.message, bag.summary, bag.error, bag.reason].find(
    (candidate): candidate is string =>
      typeof candidate === "string" && candidate.trim().length > 0
  );

  return { reason: reason?.trim() };
}

function parseJson(text: string): unknown {
  const trimmed = text.trim();
  if (!trimmed.startsWith("{") && !trimmed.startsWith("[")) return undefined;
  try {
    return JSON.parse(trimmed);
  } catch {
    return undefined;
  }
}

/**
 * Flags a refused Revit call as an MCP error and states the reason on the first
 * line, so the model sees the verdict without parsing the payload. Results that
 * are already errors, or that carry no refusal flag, are returned untouched.
 */
export function normalizeToolResult(toolName: string, result: ToolResult): ToolResult {
  if (!result || result.isError === true || !Array.isArray(result.content)) {
    return result;
  }

  for (const entry of result.content) {
    if (entry?.type !== "text" || typeof entry.text !== "string") continue;

    const parsed = parseJson(entry.text);
    if (parsed === undefined) continue;

    const refusal = findRefusal(parsed);
    if (!refusal) continue;

    const reason = refusal.reason ?? "инструмент не выполнил операцию";
    return {
      ...result,
      isError: true,
      content: result.content.map((item) =>
        item === entry
          ? { ...item, text: `${toolName} не выполнен: ${reason}\n${entry.text}` }
          : item
      ),
    };
  }

  return result;
}

/**
 * Wraps a tool handler so its result passes through {@link normalizeToolResult},
 * and so a thrown error becomes an MCP error instead of a plain-text "result"
 * the model reads as success.
 */
export function wrapToolHandler<T extends (...args: never[]) => unknown>(
  toolName: string,
  handler: T
): T {
  return (async (...args: never[]) => {
    try {
      const result = await handler(...args);
      return normalizeToolResult(toolName, result as ToolResult);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      return {
        content: [{ type: "text", text: `${toolName} не выполнен: ${message}` }],
        isError: true,
      };
    }
  }) as unknown as T;
}
