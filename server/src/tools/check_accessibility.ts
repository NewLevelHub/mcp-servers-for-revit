import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import db from "../database/db.js";
import {
  formatNormAuditReport,
  runNormAudit,
} from "../normatives/normAudit/index.js";

/**
 * Нормоконтроль доступности МГН (СП РК 3.06-101-2012*) — the МГН subset of
 * run_norm_audit as a standalone tool. Selecting topics=["мгн"] runs only the
 * accessibility checkers, including ramps and door maneuvering geometry.
 */
export function registerCheckAccessibilityTool(server: McpServer) {
  server.tool(
    "check_accessibility",
    "Нормоконтроль доступности МГН по СП РК 3.06-101-2012*: зона разворота кресла-коляски 1,5 м " +
      "(п. 4.3.2.43) в тамбурах и доступных санузлах, ширина коридоров 1,5/1,8 м (п. 4.3.2.38, 4.2.3), " +
      "ширина дверей доступных путей 0,9 м (п. 4.3.2.14), габариты доступных санузлов (п. 4.3.3.14). " +
      "Проверяет уклоны пандусов 5% (либо 8% при явно отмеченном исключении и подъёме ≤800 мм) и " +
      "зоны маневрирования перед дверьми 1,2/1,5 × 1,5 м по направлению открывания. " +
      "Каждая находка содержит цитату пункта нормы; отсутствующие достоверные измерения возвращаются как skipped. " +
      "mode=highlight заливает помещения-нарушители красным (create_filled_regions) и красит двери " +
      "через operate_element SetColor. Санузлы проверяются только с пометкой МГН/доступный/универсальный " +
      "в имени — обычные квартирные санузлы не флагуются. Findings также входят в общий run_norm_audit.",
    {
      levelName: z
        .string()
        .optional()
        .describe("Level filter, e.g. «1 этаж». Omit to use the current view level."),
      scope: z
        .enum(["floor", "project"])
        .optional()
        .default("floor")
        .describe("'floor' (default) checks one level; 'project' checks all levels."),
      mode: z
        .enum(["report", "highlight"])
        .optional()
        .default("report")
        .describe(
          "'report' data only; 'highlight' also paints violations on the active plan (red Filled Region for rooms, red override for doors)."
        ),
      includeCompliant: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include compliant findings in the report (default: violations only)."),
    },
    async (args) => {
      try {
        const result = await runNormAudit(
          {
            levelName: args.levelName,
            scope: args.scope ?? "floor",
            topics: ["мгн"],
            mode: args.mode ?? "report",
            includeCompliant: args.includeCompliant ?? false,
          },
          { db }
        );

        const report = formatNormAuditReport(result);
        const jsonPayload = {
          success: result.success,
          message: result.message,
          scope: result.scope,
          levelName: result.levelName,
          mode: result.mode,
          summary: result.summary,
          findings: result.findings,
          skippedRules: result.skippedRules,
          checks: result.checks,
          highlightedCount: result.highlightedCount,
          filledRegionCount: result.filledRegionCount,
          doorHighlightCount: result.doorHighlightCount,
          warnings: result.warnings,
        };

        return {
          content: [
            { type: "text" as const, text: report },
            { type: "text" as const, text: JSON.stringify(jsonPayload) },
          ],
          isError: !result.success,
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `check_accessibility failed: ${
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
