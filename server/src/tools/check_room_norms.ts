import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import db from "../database/db.js";
import { resolveRoomAreaLimitsFromLibrary } from "../normatives/normAudit/resolveRoomAreaLimits.js";
import { resolveRoomHeightLimitFromLibrary } from "../normatives/normAudit/resolveRoomHeightLimit.js";
import { resolveStoreyHeightLimitFromLibrary } from "../normatives/normAudit/resolveStoreyHeightLimit.js";
import {
  runRoomAreaCheck,
  runRoomHeightCheck,
  runStoreyHeightCheck,
} from "../normatives/normAudit/runners.js";

/**
 * REV-57 — room area / height / storey height norm checks.
 * Area from export_room_data; room height from level ΔZ − floor slab
 * (not raw Room UnboundedHeight / 8' default); storey height from export_tep_data ΔZ.
 */
export function registerCheckRoomNormsTool(server: McpServer) {
  server.tool(
    "check_room_norms",
    "Check room areas, room heights, and storey heights against norms (REV-57). " +
      "Area: export_room_data (m²). Room height: clear height ≈ ΔZ between levels minus floor slab " +
      "(ignores Revit default Room Limit Offset 8'/2438 mm). Storey height: export_tep_data level ΔZ. " +
      "Room types matched by name/department (жилая, кухня, санузел, спальня). " +
      "Limits from the local norm library unless overridden. " +
      "Also available inside run_norm_audit as checkTypes room_area_min, room_height_min, storey_height.",
    {
      checks: z
        .array(z.enum(["area", "height", "storey"]))
        .optional()
        .default(["area", "height", "storey"])
        .describe("Which checks to run (default: all three)."),
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Optional level filter for room checks, e.g. «1 этаж»."),
      includeCompliant: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include compliant items in JSON payloads."),
      nearLimitToleranceAreaM2: z
        .number()
        .nonnegative()
        .optional()
        .default(0.5),
      nearLimitToleranceMm: z
        .number()
        .nonnegative()
        .optional()
        .default(50),
    },
    async (args) => {
      const checks = args.checks ?? ["area", "height", "storey"];
      const levelName = args.levelName ?? "";
      const sections: string[] = ["## Проверка площадей / высот помещений и этажей", ""];
      const payload: Record<string, unknown> = { success: true, checks: {} };
      let hadError = false;

      try {
        if (checks.includes("area")) {
          const limits = resolveRoomAreaLimitsFromLibrary(db);
          if (limits.length === 0) {
            sections.push("### Площадь помещений", "Нет числовых норм площади в библиотеке.", "");
            hadError = true;
          } else {
            const result = await runRoomAreaCheck({
              levelName,
              includeCompliant: args.includeCompliant ?? false,
              limits,
              nearLimitToleranceM2: args.nearLimitToleranceAreaM2 ?? 0.5,
            });
            sections.push(
              "### Площадь помещений",
              result.message,
              `- Нарушений: **${result.violations.length}**`,
              `- Пограничных: **${result.nearLimit.length}**`,
              ""
            );
            if (result.warnings.length) {
              sections.push("Предупреждения:", ...result.warnings.map((w) => `- ${w}`), "");
            }
            payload.checks = {
              ...(payload.checks as object),
              area: result,
            };
            if (!result.success) hadError = true;
          }
        }

        if (checks.includes("height")) {
          const limit = resolveRoomHeightLimitFromLibrary(db);
          if (!limit) {
            sections.push("### Высота помещений", "Нет числовой нормы высоты в библиотеке.", "");
            hadError = true;
          } else {
            const result = await runRoomHeightCheck({
              levelName,
              includeCompliant: args.includeCompliant ?? false,
              minHeightMm: limit.minHeightMm,
              source: limit.source,
              nearLimitToleranceMm: args.nearLimitToleranceMm ?? 50,
            });
            sections.push(
              "### Высота помещений",
              result.message,
              `- Норма: **≥ ${limit.minHeightMm} мм**`,
              `- Нарушений: **${result.violations.length}**`,
              ""
            );
            payload.checks = {
              ...(payload.checks as object),
              height: result,
            };
            if (!result.success) hadError = true;
          }
        }

        if (checks.includes("storey")) {
          const limit = resolveStoreyHeightLimitFromLibrary(db);
          if (!limit) {
            sections.push("### Высота этажа", "Нет числовой нормы высоты этажа в библиотеке.", "");
            hadError = true;
          } else {
            const result = await runStoreyHeightCheck({
              minStoreyHeightMm: limit.minStoreyHeightMm,
              source: limit.source,
              nearLimitToleranceMm: args.nearLimitToleranceMm ?? 50,
            });
            sections.push(
              "### Высота этажа",
              result.message,
              `- Норма: **≥ ${limit.minStoreyHeightMm} мм**`,
              `- Нарушений: **${result.violations.length}**`,
              ""
            );
            payload.checks = {
              ...(payload.checks as object),
              storey: result,
            };
            if (!result.success) hadError = true;
          }
        }

        payload.success = !hadError;

        return {
          content: [
            { type: "text", text: sections.join("\n") },
            { type: "text", text: JSON.stringify(payload) },
          ],
          isError: hadError,
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `check_room_norms failed: ${
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
