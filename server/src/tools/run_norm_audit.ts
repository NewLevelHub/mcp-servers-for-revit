import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import db from "../database/db.js";
import {
  formatNormAuditReport,
  runNormAudit,
} from "../normatives/normAudit/index.js";

/**
 * REV-54 Phase 1 — unified measurable-norm audit over existing check_* tools.
 */
export function registerRunNormAuditTool(server: McpServer) {
  server.tool(
    "run_norm_audit",
    "Unified measurable-norm audit for the open Revit floor/project (REV-54). " +
      "One call runs existing checkers: evacuation corridor width, room depth (from norm library), " +
      "balcony/loggia/pier min dimensions, fire doors (parameter + schedule note), " +
      "door width (nominal), tambour size, room area/height/storey, " +
      "window sill height, opening height, stairs/ramps/railings, " +
      "МГН accessibility per СП РК 3.06-101-2012* (wheelchair turning 1,5 м, corridor 1,5/1,8 м, " +
      "clear door width 0,9 м, maneuvering zones, ramp slopes, accessible WC dimensions; " +
      "topics=[\"мгн\"] runs only these — see also check_accessibility). " +
      "Returns summary + findings[] with document/clause/quote + actualMm/requiredMm, and skippedRules[] " +
      "for checks not yet implemented (e.g. door clear width «в свету») " +
      "plus skipped findings when the model lacks a trustworthy measurement. " +
      "Does NOT claim full GOST coverage — only measurable checks with a checker. " +
      "Prefer this over calling multiple check_* when the user says «проверь этаж по нормам». " +
      "mode=highlight runs the same audit then paints violations: rooms via create_filled_regions " +
      "(Annotate → Цветовая область), doors and ramps via operate_element SetColor red.",
    {
      levelName: z
        .string()
        .optional()
        .describe(
          "Level filter, e.g. «1 этаж». Omit to use current view level when scope=floor."
        ),
      scope: z
        .enum(["floor", "project"])
        .optional()
        .default("floor")
        .describe(
          "'floor' (default) filters by levelName / current view; 'project' checks all levels."
        ),
      topics: z
        .array(z.string())
        .optional()
        .describe(
          "Optional filter, e.g. [\"эвак. коридор\", \"лоджия\", \"ПД\"]. Omit to run all Phase-1 checkers."
        ),
      mode: z
        .enum(["report", "highlight"])
        .optional()
        .default("report")
        .describe(
          "'report' data only; 'highlight' also paints violations on the active plan (Filled Region for rooms, red Override for doors and ramps)."
        ),
      includeCompliant: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include compliant findings in the report (default: violations only)."),
      nearLimitToleranceMm: z
        .number()
        .nonnegative()
        .optional()
        .default(100)
        .describe(
          "Evacuation width: classify shortfalls within this many mm as nearLimit (default 100)."
        ),
    },
    async (args) => {
      try {
        const result = await runNormAudit(
          {
            levelName: args.levelName,
            scope: args.scope ?? "floor",
            topics: args.topics,
            mode: args.mode ?? "report",
            includeCompliant: args.includeCompliant ?? false,
            nearLimitToleranceMm: args.nearLimitToleranceMm ?? 100,
          },
          { db }
        );

        const report = formatNormAuditReport(result);

        // Compact JSON for the agent: drop huge compliant lists unless requested
        const jsonPayload = {
          success: result.success,
          message: result.message,
          scope: result.scope,
          levelName: result.levelName,
          mode: result.mode,
          scopeNote: result.scopeNote,
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
            {
              type: "text" as const,
              text: JSON.stringify(jsonPayload),
            },
          ],
          isError: !result.success,
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `run_norm_audit failed: ${
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
