import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import db from "../database/db.js";
import { resolveWindowSillLimitFromLibrary } from "../normatives/normAudit/resolveWindowSill.js";
import { resolveOpeningHeightLimitFromLibrary } from "../normatives/normAudit/resolveOpeningHeight.js";
import {
  runOpeningHeightCheck,
  runWindowSillCheck,
} from "../normatives/normAudit/runners.js";
import type { NormAuditSource } from "../normatives/normAudit/types.js";

/**
 * REV-58 — window sill height + opening height norm checks.
 * Data from get_opening_geometry_info; limits from the norm library unless overridden.
 */
export function registerCheckWindowOpeningsTool(server: McpServer) {
  server.tool(
    "check_window_openings",
    "Check window sill height and door/window opening height against norms (REV-58). " +
      "Sill: INSTANCE_SILL_HEIGHT / sillHeightMm vs min from library (or minSillHeightMm). " +
      "Opening height: DOOR_HEIGHT / WINDOW_HEIGHT; by default only egress doors vs " +
      "«высота эвакуационных выходов в свету» (≥ 1,9 м typical). " +
      "Also available inside run_norm_audit as checkTypes window_sill_height, opening_height.",
    {
      checks: z
        .array(z.enum(["sill", "opening"]))
        .optional()
        .default(["sill", "opening"])
        .describe("Which checks to run (default: both)."),
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Optional level filter, e.g. «1 этаж»."),
      minSillHeightMm: z
        .number()
        .positive()
        .optional()
        .describe("Override sill minimum (mm). Omit to resolve from library."),
      minOpeningHeightMm: z
        .number()
        .positive()
        .optional()
        .describe(
          "Override opening height minimum (mm). Omit to resolve from library."
        ),
      source: z
        .object({
          document: z.string(),
          clause: z.string(),
          quote: z.string(),
          page: z.number().int().positive().optional(),
        })
        .optional()
        .describe("Override citation when passing min*Mm manually."),
      includeCompliant: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include compliant items in JSON payloads."),
      nearLimitToleranceMm: z
        .number()
        .nonnegative()
        .optional()
        .default(50),
      egressDoorsOnly: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "For opening height: only egress doors (default). false = all openings."
        ),
    },
    async (args) => {
      const checks = args.checks ?? ["sill", "opening"];
      const levelName = args.levelName ?? "";
      const sections: string[] = [
        "## Проверка подоконника / высоты проёмов",
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
        if (checks.includes("sill")) {
          let minSillHeightMm = args.minSillHeightMm;
          let source = manualSource;
          if (minSillHeightMm === undefined) {
            const resolved = resolveWindowSillLimitFromLibrary(db);
            if (!resolved) {
              sections.push(
                "### Высота подоконника",
                "В библиотеке нет числовой нормы высоты подоконника. " +
                  "Сделайте seed или передайте minSillHeightMm.",
                ""
              );
              hadError = true;
            } else {
              minSillHeightMm = resolved.minSillHeightMm;
              source = resolved.source;
            }
          }
          if (minSillHeightMm != null && minSillHeightMm > 0) {
            const result = await runWindowSillCheck({
              levelName,
              includeCompliant: args.includeCompliant ?? false,
              minSillHeightMm,
              source: source ?? {
                document: "—",
                clause: "",
                quote: "Значение передано вручную без цитаты норматива.",
              },
              nearLimitToleranceMm: args.nearLimitToleranceMm ?? 50,
            });
            sections.push(
              "### Высота подоконника",
              result.message,
              `- Нарушений: **${result.violations.length}**`,
              `- Пограничных: **${result.nearLimit.length}**`,
              ""
            );
            if (result.warnings.length) {
              sections.push(
                "Предупреждения:",
                ...result.warnings.map((w) => `- ${w}`),
                ""
              );
            }
            payload.checks = {
              ...(payload.checks as object),
              sill: result,
            };
            if (!result.success) hadError = true;
          }
        }

        if (checks.includes("opening")) {
          let minHeightMm = args.minOpeningHeightMm;
          let source = manualSource;
          if (minHeightMm === undefined) {
            const resolved = resolveOpeningHeightLimitFromLibrary(db);
            if (!resolved) {
              sections.push(
                "### Высота проёма",
                "В библиотеке нет числовой нормы высоты проёма. " +
                  "Сделайте seed или передайте minOpeningHeightMm.",
                ""
              );
              hadError = true;
            } else {
              minHeightMm = resolved.minHeightMm;
              source = resolved.source;
            }
          }
          if (minHeightMm != null && minHeightMm > 0) {
            const result = await runOpeningHeightCheck({
              levelName,
              includeCompliant: args.includeCompliant ?? false,
              minHeightMm,
              source: source ?? {
                document: "—",
                clause: "",
                quote: "Значение передано вручную без цитаты норматива.",
              },
              nearLimitToleranceMm: args.nearLimitToleranceMm ?? 50,
              egressDoorsOnly: args.egressDoorsOnly ?? true,
            });
            sections.push(
              "### Высота проёма",
              result.message,
              `- Нарушений: **${result.violations.length}**`,
              `- Пограничных: **${result.nearLimit.length}**`,
              ""
            );
            if (result.warnings.length) {
              sections.push(
                "Предупреждения:",
                ...result.warnings.map((w) => `- ${w}`),
                ""
              );
            }
            payload.checks = {
              ...(payload.checks as object),
              opening: result,
            };
            if (!result.success) hadError = true;
          }
        }

        payload.success = !hadError;
        return {
          content: [
            { type: "text", text: sections.join("\n") },
            { type: "text", text: JSON.stringify(payload, null, 2) },
          ],
          isError: hadError,
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `check_window_openings failed: ${
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
