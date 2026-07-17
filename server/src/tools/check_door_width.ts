import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import db from "../database/db.js";
import { resolveDoorWidthLimitFromLibrary } from "../normatives/normAudit/resolveDoorWidth.js";
import {
  runDoorWidthCheck,
  type DoorWidthRunnerResult,
} from "../normatives/normAudit/runners.js";
import type { NormAuditSource } from "../normatives/normAudit/types.js";

/**
 * REV-56 — door / opening width vs norm.
 * Compares a trustworthy clear-opening parameter/calculation from
 * get_door_egress_info against a minimum for doors on egress paths.
 * Accessories are excluded; nominal-only families are explicit skipped results.
 */
function formatDoorWidthReport(result: DoorWidthRunnerResult): string {
  const lines: string[] = [
    "## Проверка ширины дверей / проёмов",
    "",
    result.message,
    "",
    `- Требуемая ширина: **≥ ${result.minWidthMm} мм**`,
    `- Проверено (на путях эвакуации): **${result.totalChecked}**`,
    `- Нарушений: **${result.violations.length}**`,
    `- Пограничных: **${result.nearLimit.length}**`,
    `- Пропущено без ширины «в свету»: **${result.unmeasured?.length ?? 0}**`,
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
    for (const door of result.violations) {
      lines.push(
        `- **${door.type || door.family || `дверь ${door.id}`}** (id ${door.id}` +
          (door.level ? `, ${door.level}` : "") +
          `) — факт **${Math.round(door.actualMm)} мм**, не хватает **${Math.round(
            door.deviationMm
          )} мм**`
      );
    }
  }

  if (result.nearLimit.length > 0) {
    lines.push("", "### Пограничные");
    for (const door of result.nearLimit) {
      lines.push(
        `- **${door.type || door.family || `дверь ${door.id}`}** (id ${door.id}) — **${Math.round(
          door.actualMm
        )} мм** (норма ${result.minWidthMm} мм)`
      );
    }
  }

  if (
    result.violations.length === 0 &&
    result.nearLimit.length === 0 &&
    result.totalChecked > 0
  ) {
    lines.push("", "Все проверенные двери соответствуют требуемой ширине.");
  }

  return lines.join("\n");
}

export function registerCheckDoorWidthTool(server: McpServer) {
  server.tool(
    "check_door_width",
    "Check clear door / opening width against a norm (REV-56). Uses a trustworthy " +
      "clear-opening parameter/calculation; nominal-only families are reported as skipped. " +
      "Resolves the minimum from the local norm library " +
      "(e.g. СП РК 3.02-101-2012 п.4.6.11 ≥ 0.9 м) unless minWidthMm is passed. " +
      "By default checks only doors on an egress path (interior apartment doors have no " +
      "single applicable minimum). Excludes откосы/наличники (REV-41). " +
      "Also available inside run_norm_audit as checkType 'door_clear_width'.",
    {
      minWidthMm: z
        .number()
        .positive()
        .optional()
        .describe(
          "Minimum required width in mm. Omit to auto-resolve from the norm library."
        ),
      source: z
        .object({
          document: z.string(),
          clause: z.string(),
          quote: z.string(),
          page: z.number().int().positive().optional(),
        })
        .optional()
        .describe("Override citation when passing minWidthMm manually."),
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Optional level filter, e.g. «1 этаж»."),
      egressOnly: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Check only doors on egress paths (default). false = check every door block against minWidthMm."
        ),
      includeCompliant: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include compliant doors in the JSON payload."),
      nearLimitToleranceMm: z
        .number()
        .nonnegative()
        .optional()
        .default(50)
        .describe("Shortfalls within this many mm are reported as nearLimit."),
    },
    async (args) => {
      try {
        let minWidthMm = args.minWidthMm;
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

        if (minWidthMm === undefined) {
          const resolved = resolveDoorWidthLimitFromLibrary(db);
          if (!resolved) {
            return {
              content: [
                {
                  type: "text",
                  text:
                    "check_door_width: в библиотеке норм нет числовой нормы ширины двери/проёма. " +
                    "Сделайте seed (extract_norm_rules_from_pdf action=seed) или передайте minWidthMm явно.",
                },
              ],
              isError: true,
            };
          }
          minWidthMm = resolved.minWidthMm;
          source = resolved.source;
        }

        const result = await runDoorWidthCheck({
          levelName: args.levelName ?? "",
          includeCompliant: args.includeCompliant ?? false,
          minWidthMm,
          source,
          nearLimitToleranceMm: args.nearLimitToleranceMm ?? 50,
          egressOnly: args.egressOnly ?? true,
        });

        const report = formatDoorWidthReport(result);
        const jsonPayload = {
          success: result.success,
          message: result.message,
          minWidthMm: result.minWidthMm,
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
            { type: "text", text: JSON.stringify(jsonPayload, null, 2) },
          ],
          isError: !result.success,
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `check_door_width failed: ${
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
