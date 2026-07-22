import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import db from "../database/db.js";
import { resolveTambourSizeLimitFromLibrary } from "../normatives/normAudit/resolveTambourSize.js";
import {
  runTambourSizeCheck,
  type TambourSizeRunnerResult,
} from "../normatives/normAudit/runners.js";
import type { NormAuditSource } from "../normatives/normAudit/types.js";

/**
 * REV-67 — entrance tambour / vestibule size vs norm.
 * v1 compares bounding width × depth from get_room_geometry_metrics against a
 * minimum side resolved from the norm library (typically 1650 mm per
 * СП РК 3.02-101-2012 п. 4.4.10.6). Rooms are matched by name/number keywords.
 */
function formatTambourSizeReport(result: TambourSizeRunnerResult): string {
  const lines: string[] = [
    "## Проверка габарита входного тамбура",
    "",
    result.message,
    "",
    `- Требуемый габарит: **≥ ${result.minSideMm} × ${result.minSideMm} мм**`,
    `- Проверено тамбуров: **${result.totalChecked}**`,
    `- Нарушений: **${result.violations.length}**`,
    `- Пограничных: **${result.nearLimit.length}**`,
  ];

  if (result.source.document && result.source.document !== "—") {
    lines.push(
      `- Норматив: **${result.source.document}**${
        result.source.clause ? `, ${result.source.clause}` : ""
      }`
    );
    if (result.source.quote) {
      const q =
        result.source.quote.length > 200
          ? `${result.source.quote.slice(0, 200)}…`
          : result.source.quote;
      lines.push(`  - «${q}»`);
    }
  }

  if (result.warnings.length > 0) {
    lines.push("", "### Предупреждения");
    for (const warning of result.warnings) lines.push(`- ${warning}`);
  }

  if (result.violations.length > 0) {
    lines.push("", "### Нарушения");
    for (const room of result.violations) {
      lines.push(
        `- **${room.name}** (id ${room.id}` +
          (room.level ? `, ${room.level}` : "") +
          `) — **${Math.round(room.widthMm)} × ${Math.round(room.depthMm)} мм**, ` +
          `мин. сторона **${Math.round(room.minSideMm)} мм**, не хватает **${Math.round(
            room.deviationMm
          )} мм**`
      );
    }
  }

  if (result.nearLimit.length > 0) {
    lines.push("", "### Пограничные");
    for (const room of result.nearLimit) {
      lines.push(
        `- **${room.name}** (id ${room.id}) — **${Math.round(room.widthMm)} × ${Math.round(
          room.depthMm
        )} мм** (норма ${result.minSideMm} × ${result.minSideMm} мм)`
      );
    }
  }

  if (
    result.violations.length === 0 &&
    result.nearLimit.length === 0 &&
    result.totalChecked > 0
  ) {
    lines.push("", "Все найденные тамбуры соответствуют требуемому габариту.");
  }

  return lines.join("\n");
}

export function registerCheckTambourSizeTool(server: McpServer) {
  server.tool(
    "check_tambour_size",
    "Check entrance tambour / vestibule size against a norm (REV-67). v1 uses " +
      "bounding room width × depth from get_room_geometry_metrics; both sides must " +
      "meet the minimum (typically 1.65 m × 1.65 m per СП РК 3.02-101-2012 п. 4.4.10.6). " +
      "Rooms are matched by name/number keywords (тамбур, vestibule, …). " +
      "Resolves the minimum from the local norm library unless minSideMm is passed. " +
      "Also available inside run_norm_audit as checkType 'tambour_size_min'.",
    {
      minSideMm: z
        .number()
        .positive()
        .optional()
        .describe(
          "Minimum required side length in mm. Omit to auto-resolve from the norm library."
        ),
      source: z
        .object({
          document: z.string(),
          clause: z.string(),
          quote: z.string(),
          page: z.number().int().positive().optional(),
        })
        .optional()
        .describe("Override citation when passing minSideMm manually."),
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Optional level filter, e.g. «1 этаж»."),
      includeCompliant: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include compliant tambours in the JSON payload."),
      nearLimitToleranceMm: z
        .number()
        .nonnegative()
        .optional()
        .default(50)
        .describe("Shortfalls within this many mm are reported as nearLimit."),
    },
    async (args) => {
      try {
        let minSideMm = args.minSideMm;
        let source: NormAuditSource = args.source
          ? {
              document: args.source.document,
              clause: args.source.clause,
              quote: args.source.quote,
              ...(args.source.page != null ? { page: args.source.page } : {}),
            }
          : {
              document: "—",
              clause: "",
              quote: "Значение передано вручную без цитаты норматива.",
            };

        if (minSideMm === undefined) {
          const resolved = resolveTambourSizeLimitFromLibrary(db);
          if (!resolved) {
            return {
              content: [
                {
                  type: "text",
                  text:
                    "check_tambour_size: в библиотеке норм нет числовой нормы габарита тамбура. " +
                    "Сделайте seed (extract_norm_rules_from_pdf action=seed) или передайте minSideMm явно.",
                },
              ],
              isError: true,
            };
          }
          minSideMm = resolved.minSideMm;
          source = resolved.source;
        }

        const result = await runTambourSizeCheck({
          levelName: args.levelName ?? "",
          includeCompliant: args.includeCompliant ?? false,
          minSideMm,
          source,
          nearLimitToleranceMm: args.nearLimitToleranceMm ?? 50,
        });

        const report = formatTambourSizeReport(result);
        const jsonPayload = {
          success: result.success,
          message: result.message,
          minSideMm: result.minSideMm,
          source: result.source,
          totalChecked: result.totalChecked,
          violations: result.violations,
          nearLimit: result.nearLimit,
          compliant: args.includeCompliant ? result.compliant : [],
          warnings: result.warnings,
        };

        return {
          content: [
            { type: "text", text: report },
            { type: "text", text: JSON.stringify(jsonPayload) },
          ],
          isError: !result.success,
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `check_tambour_size failed: ${
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
