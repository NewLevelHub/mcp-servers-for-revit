import { z } from "zod";

/**
 * Why every tool validates its arguments strictly.
 *
 * `server.tool(name, description, shape, handler)` builds `z.object(shape)`,
 * and a plain Zod object *strips* keys it does not know. The call then succeeds
 * with the unknown argument thrown away, which is the worst of the three
 * possible outcomes:
 *
 *   - `get_current_view_elements({ categories: ["Rooms"] })` — the real spelling
 *     is `modelCategoryList` — returned all 58 elements of the view instead of
 *     the 12 rooms. No error, no warning; the model read it as "the filter
 *     worked, the view really does hold 58 rooms" and carried on (19.08.2026).
 *
 * A rejected call costs one round and the model fixes the name itself: it did
 * exactly that when `set_elements_parameters` refused `updates` and it retried
 * with `edits`. A silently dropped filter costs the whole answer.
 *
 * Two things change here, both for all tools at once:
 *   1. unknown keys are refused, with the valid names in the message;
 *   2. the published JSON Schema carries `additionalProperties: false`, so the
 *      model sees the closed list before it calls rather than after.
 *
 * Registration goes through `tools/register.ts`, so no tool module has to opt
 * in — and none can opt out by accident.
 */

/** The `{ name: zodType }` bag a tool module passes to `server.tool`. */
export type ToolShape = Record<string, z.ZodTypeAny>;

/**
 * Extra sentence for tools whose refusals we have watched the model trip over
 * in the field. Keyed by tool name; keep it to cases with evidence, not to
 * every tool — an error message nobody reads is worth nothing.
 */
const ARGUMENT_HINTS: Record<string, string> = {
  // Watched twice on 19.08.2026: the model invented `colorScheme` and then
  // `colorGroups`, both carrying a colour per element id. There is no
  // per-element mode — a different colour per room comes from grouping by a
  // parameter that is unique per room, which is why the hint says so instead
  // of just listing the four valid names.
  color_elements:
    "Цвет назначается не поэлементно, а по значению параметра: categoryName + parameterName. " +
    "Разные цвета всем помещениям — parameterName «Имя» или «Номер» (значения уникальны), " +
    "палитра — customColors в порядке групп.",
  set_elements_parameters:
    "Либо edits:[{elementId, parameters:{«Имя»:«значение»}}], либо elementIds[] + parameters{}.",
  get_current_view_elements:
    "Категории модели — modelCategoryList:[«OST_Rooms»], аннотации — annotationCategoryList:[«OST_RoomTags»].",
};

/**
 * The message a refused argument produces. Zod reports the offending key itself
 * (the issue carries `keys`), so this adds what Zod cannot know: what the tool
 * *does* accept.
 */
export function unknownArgumentMessage(
  toolName: string,
  shape: ToolShape
): string {
  const known = Object.keys(shape);
  const list = known.length > 0 ? known.join(", ") : "(у инструмента нет параметров)";
  const hint = ARGUMENT_HINTS[toolName];

  return (
    `Неизвестный параметр для ${toolName}. Допустимые: ${list}.` +
    (hint ? ` ${hint}` : "")
  );
}

/**
 * The tool's shape as a closed object.
 *
 * Stays a `ZodObject` on purpose. The MCP SDK reaches for `.shape` when it
 * turns the schema into JSON Schema for `tools/list`, and anything it cannot
 * recognise as an object — a `z.preprocess` wrapper, for one — publishes as an
 * *empty* schema, which would hide every parameter from the model.
 */
export function strictInputSchema(
  toolName: string,
  shape: ToolShape
): z.ZodObject<ToolShape, "strict"> {
  return z.object(shape).strict(unknownArgumentMessage(toolName, shape));
}

/**
 * Whether `server.tool` was handed a parameter shape at all.
 *
 * Mirrors the SDK's own test. Tools registered without a shape keep
 * `inputSchema: undefined` there, and that is load-bearing: it is what lets a
 * client call them with no `arguments` at all. Handing them an empty strict
 * object instead would refuse those calls, so they are left alone.
 */
export function isToolShape(value: unknown): value is ToolShape {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return false;
  }
  if (isZodSchema(value)) return false;

  const values = Object.values(value as Record<string, unknown>);
  return values.length > 0 && values.every(isZodSchema);
}

function isZodSchema(value: unknown): boolean {
  if (typeof value !== "object" || value === null) return false;
  const bag = value as Record<string, unknown>;
  return (
    bag._def !== undefined ||
    bag._zod !== undefined ||
    typeof bag.parse === "function"
  );
}
