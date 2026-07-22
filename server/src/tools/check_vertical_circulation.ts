import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import db from "../database/db.js";
import {
  resolveRampLimitsFromLibrary,
  resolveRailingHeightLimitFromLibrary,
  resolveStairRiserTreadLimitsFromLibrary,
  resolveStairWidthLimitFromLibrary,
} from "../normatives/normAudit/resolveVerticalCirculation.js";
import {
  runRailingHeightCheck,
  runRampCheck,
  runStairRiserTreadCheck,
  runStairWidthCheck,
} from "../normatives/normAudit/runners.js";
import type { NormAuditSource } from "../normatives/normAudit/types.js";

/**
 * REV-59 — stairs / ramps / railings vs library norms.
 * Data from get_vertical_circulation_info.
 */
export function registerCheckVerticalCirculationTool(server: McpServer) {
  server.tool(
    "check_vertical_circulation",
    "Check stair width, riser/tread, ramp slope/width, railing height (REV-59). " +
      "Limits from the norm library unless overridden. " +
      "Also available inside run_norm_audit as checkTypes stair_width, " +
      "stair_riser_tread, ramp_slope_width, railing_height.",
    {
      checks: z
        .array(
          z.enum(["stair_width", "stair_riser_tread", "ramp", "railing"])
        )
        .optional()
        .default(["stair_width", "stair_riser_tread", "ramp", "railing"])
        .describe("Which checks to run (default: all)."),
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Optional level filter."),
      minStairWidthMm: z.number().positive().optional(),
      maxRiserMm: z.number().positive().optional(),
      minTreadMm: z.number().positive().optional(),
      minRampWidthMm: z.number().positive().optional(),
      maxRampSlopePercent: z.number().positive().optional(),
      minRailingHeightMm: z.number().positive().optional(),
      source: z
        .object({
          document: z.string(),
          clause: z.string(),
          quote: z.string(),
          page: z.number().int().positive().optional(),
        })
        .optional(),
      includeCompliant: z.boolean().optional().default(false),
    },
    async (args) => {
      const checks = args.checks ?? [
        "stair_width",
        "stair_riser_tread",
        "ramp",
        "railing",
      ];
      const levelName = args.levelName ?? "";
      const sections: string[] = [
        "## Проверка лестниц / пандусов / ограждений",
        "",
      ];
      const payload: Record<string, unknown> = { success: true, checks: {} };
      let hadError = false;

      const manualSource: NormAuditSource | undefined = args.source
        ? {
            document: args.source.document,
            clause: args.source.clause,
            quote: args.source.quote,
            ...(args.source.page != null ? { page: args.source.page } : {}),
          }
        : undefined;

      try {
        if (checks.includes("stair_width")) {
          let minWidthMm = args.minStairWidthMm;
          let source = manualSource;
          if (minWidthMm === undefined) {
            const resolved = resolveStairWidthLimitFromLibrary(db);
            if (!resolved) {
              sections.push(
                "### Ширина марша — skipped (нет нормы в библиотеке)"
              );
              (payload.checks as Record<string, unknown>).stair_width = {
                skipped: true,
              };
            } else {
              minWidthMm = resolved.minWidthMm;
              source = resolved.source;
            }
          }
          if (minWidthMm != null && source) {
            const result = await runStairWidthCheck({
              levelName,
              includeCompliant: args.includeCompliant ?? false,
              minWidthMm,
              source,
            });
            sections.push(`### Ширина марша`, "", result.message, "");
            (payload.checks as Record<string, unknown>).stair_width = result;
            if (!result.success) hadError = true;
          }
        }

        if (checks.includes("stair_riser_tread")) {
          let maxRiserMm = args.maxRiserMm;
          let minTreadMm = args.minTreadMm;
          let source = manualSource;
          if (maxRiserMm === undefined && minTreadMm === undefined) {
            const resolved = resolveStairRiserTreadLimitsFromLibrary(db);
            if (!resolved) {
              sections.push(
                "### Подступенок/проступь — skipped (нет нормы в библиотеке)"
              );
              (payload.checks as Record<string, unknown>).stair_riser_tread = {
                skipped: true,
              };
            } else {
              maxRiserMm = resolved.maxRiserMm;
              minTreadMm = resolved.minTreadMm;
              source =
                resolved.riserSource ?? resolved.treadSource ?? manualSource;
            }
          }
          if (
            (maxRiserMm != null || minTreadMm != null) &&
            (source || manualSource)
          ) {
            const result = await runStairRiserTreadCheck({
              levelName,
              includeCompliant: args.includeCompliant ?? false,
              maxRiserMm,
              minTreadMm,
              source: source ??
                manualSource ?? {
                  document: "—",
                  clause: "",
                  quote: "Норма ступени.",
                },
            });
            sections.push(`### Подступенок / проступь`, "", result.message, "");
            (payload.checks as Record<string, unknown>).stair_riser_tread =
              result;
            if (!result.success) hadError = true;
          }
        }

        if (checks.includes("ramp")) {
          let minWidthMm = args.minRampWidthMm;
          let maxSlopePercent = args.maxRampSlopePercent;
          let source = manualSource;
          if (minWidthMm === undefined && maxSlopePercent === undefined) {
            const resolved = resolveRampLimitsFromLibrary(db);
            if (!resolved) {
              sections.push(
                "### Пандус — skipped (нет нормы в библиотеке)"
              );
              (payload.checks as Record<string, unknown>).ramp = {
                skipped: true,
              };
            } else {
              minWidthMm = resolved.minWidthMm;
              maxSlopePercent = resolved.maxSlopePercent;
              source =
                resolved.widthSource ?? resolved.slopeSource ?? manualSource;
            }
          }
          if (
            (minWidthMm != null || maxSlopePercent != null) &&
            (source || manualSource)
          ) {
            const result = await runRampCheck({
              levelName,
              includeCompliant: args.includeCompliant ?? false,
              minWidthMm,
              maxSlopePercent,
              source: source ??
                manualSource ?? {
                  document: "—",
                  clause: "",
                  quote: "Норма пандуса.",
                },
            });
            sections.push(`### Пандус`, "", result.message, "");
            (payload.checks as Record<string, unknown>).ramp = result;
            if (!result.success) hadError = true;
          }
        }

        if (checks.includes("railing")) {
          let minHeightMm = args.minRailingHeightMm;
          let source = manualSource;
          if (minHeightMm === undefined) {
            const resolved = resolveRailingHeightLimitFromLibrary(db);
            if (!resolved) {
              sections.push(
                "### Ограждение — skipped (нет нормы в библиотеке)"
              );
              (payload.checks as Record<string, unknown>).railing = {
                skipped: true,
              };
            } else {
              minHeightMm = resolved.minHeightMm;
              source = resolved.source;
            }
          }
          if (minHeightMm != null && source) {
            const result = await runRailingHeightCheck({
              levelName,
              includeCompliant: args.includeCompliant ?? false,
              minHeightMm,
              source,
            });
            sections.push(`### Ограждение`, "", result.message, "");
            (payload.checks as Record<string, unknown>).railing = result;
            if (!result.success) hadError = true;
          }
        }
      } catch (error) {
        hadError = true;
        sections.push(
          `Ошибка: ${error instanceof Error ? error.message : String(error)}`
        );
      }

      payload.success = !hadError;
      sections.push("", "```json", JSON.stringify(payload), "```");

      return {
        content: [{ type: "text", text: sections.join("\n") }],
        isError: hadError,
      };
    }
  );
}
