import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

/** What `color_splash` hands back from the plugin. */
export type ColorSplashGroup = {
  parameterValue?: string;
  count?: number;
  color?: { r?: number; g?: number; b?: number };
};

export type ColorSplashResponse = {
  success?: boolean;
  message?: string;
  mode?: string;
  schemeName?: string;
  parameterName?: string;
  totalElements?: number;
  coloredElements?: number;
  coloredGroups?: number;
  skippedValues?: string[];
  /** Room numbers Revit gives no area to; see `colorFillBlocked`. */
  roomsWithoutArea?: string[];
  /**
   * Revit will not run the colour-fill calculation for the view while such
   * rooms exist, and paints every room with one fallback hatch instead. The
   * scheme applies and every read-back passes, so nothing else in the payload
   * shows it.
   */
  colorFillBlocked?: boolean;
  results?: ColorSplashGroup[];
};

export type ColorToolResult = {
  content: { type: "text"; text: string }[];
  isError?: boolean;
};

/**
 * Turn the plugin's answer into a result the model cannot misread.
 *
 * Three states used to come back indistinguishable from success, and all three
 * were watched in the field on 20.08.2026:
 *
 *   - an outright refusal («Category 'Помещения' not found») was returned as a
 *     plain-text result with no `isError`, so the feedback log recorded
 *     `ok: true` and the model treated it as done;
 *   - `colorFillBlocked` — the scheme lands but Revit refuses to compute the
 *     fill, so the architect is looking at flat pink while the answer says the
 *     plan is coloured;
 *   - a palette of three colours collapsing onto a single group, because every
 *     room carried the same «Имя». One colour on the plan, and the answer still
 *     said «красный, синий и зелёный чередуются».
 *
 * The first two are errors: nothing the user asked for is on screen. The third
 * is a real result with a warning, and the warning goes first — the model
 * summarises from the top of the payload.
 *
 * The «не выполнен:» prefix is load-bearing beyond prose: the assistant bridge
 * matches it to mark the step failed (`toolResultFailed`), which is what the
 * dislike log reads for its `ok` field.
 */
export function formatColorResult(
  response: ColorSplashResponse | null | undefined,
  requestedColors = 0
): ColorToolResult {
  if (!response || response.success !== true) {
    const reason = response?.message?.trim() || "Revit не выполнил раскраску.";
    return {
      content: [{ type: "text", text: `color_elements не выполнен: ${reason}` }],
      isError: true,
    };
  }

  const groups = response.results ?? [];
  // The plugin reports what actually landed, which can be less than it was
  // asked for. Repeating totalElements as if it were the result is how «все 36
  // помещений, у каждого свой цвет» got said over a plan Revit had painted in
  // one flat hatch (19.08.2026).
  const colored = response.coloredElements ?? response.totalElements;
  const groupCount = response.coloredGroups ?? groups.length;

  if (response.colorFillBlocked) {
    const blocked = response.roomsWithoutArea ?? [];
    const blockedLine =
      blocked.length > 0
        ? `Мешают ${blocked.length} помещ. без площади: №${blocked.slice(0, 15).join(", №")}` +
          (blocked.length > 15 ? " и др." : "") + ".\n"
        : "";

    return {
      content: [
        {
          type: "text",
          text:
            "color_elements не выполнен: цветовая схема создана и назначена виду, но Revit " +
            "НЕ рассчитает по ней заливку — на плане останется одна штриховка вместо цветов.\n" +
            blockedLine +
            "Причина — незамкнутый контур или избыточное помещение. Сначала это в модели, " +
            "потом раскраска.\n" +
            "Не сообщай пользователю, что план раскрашен: он не раскрашен.",
        },
      ],
      isError: true,
    };
  }

  const lines: string[] = [];

  // Asked for a palette, got one bucket: every element takes the first colour.
  if (requestedColors > 1 && groupCount <= 1) {
    lines.push(
      `⚠ ОДИН ЦВЕТ НА ВСЕХ: по параметру «${response.parameterName ?? "?"}» ` +
        `все ${response.totalElements ?? colored} элем. попали в одну группу, ` +
        `поэтому из ${requestedColors} запрошенных цветов применён только первый. ` +
        "Разные цвета получатся только по параметру с разными значениями — " +
        "для помещений это «Номер» / «Number». Не говори, что цвета чередуются."
    );
  }

  lines.push(
    `Colored ${colored} of ${response.totalElements} elements across ${groupCount} groups.`
  );
  if (response.message) lines.push(response.message);
  if (response.skippedValues?.length) {
    lines.push(
      `Left uncolored — Revit rejected these values: ${response.skippedValues.join(", ")}.`
    );
  }

  lines.push("", "Parameter Value Groups:");
  for (const group of groups) {
    const rgb = group.color ?? {};
    lines.push(
      `- "${group.parameterValue}": ${group.count} elements colored with RGB(${rgb.r}, ${rgb.g}, ${rgb.b})`
    );
  }

  return { content: [{ type: "text", text: lines.join("\n") }] };
}

export function registerColorElementsTool(server: McpServer) {
  server.tool(
      "color_elements",
      "Color elements via View Color Scheme (цветовая схема) for Rooms, or Override Graphics for other categories. " +
      "Colour follows a parameter value, never an element id: there is no per-element list. " +
      "«Каждому помещению свой цвет» = group by a parameter whose value is unique per room — for rooms that is " +
      "parameterName «Номер» / «Number», NOT «Имя» / «Name», which is usually identical on every room and yields one colour. " +
      "Category and parameter names may be given in Russian or English: both are resolved whatever the Revit UI language. " +
      "NOT Annotate → Filled Region («Цветовая область») — for that use create_filled_regions. Prefer create_filled_regions for norm-violation solid fills that look like the Annotate UI; use this tool for soft multi-color schemes by parameter (as on plans with «Номер» / «Назначение»).",
      {
        categoryName: z
            .string()
            .describe("Revit category, e.g. 'Помещения' or 'Rooms' for room color fill scheme; 'Walls', 'Doors' for override splash. Either language works."),
        parameterName: z
            .string()
            .describe("Parameter to group/color by. A different colour per room comes from 'Номер' / 'Number'; 'Имя' / 'Name' puts every room sharing a name into one colour. Either language works."),
        useGradient: z
            .boolean()
            .optional()
            .default(false)
            .describe("Whether to use a gradient color scheme instead of random colors"),
        customColors: z
            .array(
                z.object({
                  r: z.number().int().min(0).max(255),
                  g: z.number().int().min(0).max(255),
                  b: z.number().int().min(0).max(255),
                })
            )
            .optional()
            .describe("Optional RGB colors in group order (first group → first color). For violations mark them first in parameter values or use Комментарии='НК-нарушение'."),
      },
      async (args) => {
        const params = args;
        try {
          const response = await withRevitConnection(async (revitClient) => {
            return await revitClient.sendCommand("color_splash", params);
          });

          return formatColorResult(
            response as ColorSplashResponse,
            args.customColors?.length ?? 0
          );
        } catch (error) {
          return {
            content: [
              {
                type: "text" as const,
                text: `color_elements не выполнен: ${
                    error instanceof Error ? error.message : String(error)
                }`,
              },
            ],
            isError: true,
          };
        }
      }
  );
}
