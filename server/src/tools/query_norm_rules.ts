import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import db from "../database/db.js";
import { normativeRuleTypeSchema } from "../normatives/types.js";
import {
  compactRulesForMcp,
  queryNormRules,
} from "../normatives/rulesStore.js";

export function registerQueryNormRulesTool(server: McpServer) {
  server.tool(
    "query_norm_rules",
    "Search the local library of saved normative rules by topic (e.g. 'ширина коридора'). Call this BEFORE checking Revit model elements against norms — it returns previously saved rules with source document, clause and original quote, so there is no need to re-read the normative PDF. Never invent normative values from memory; use rules returned by this tool. If nothing is found, retry 1-2 times with synonyms or related terms (including Kazakh, e.g. дәліз for коридор) before telling the user there are no rules.",
    {
      topic: z
        .string()
        .min(1)
        .describe(
          "Topic in natural language, e.g. 'ширина коридора' or 'площадь жилой комнаты'."
        ),
      document: z
        .string()
        .optional()
        .describe("Optional document filter, e.g. СП РК 3.02-101."),
      ruleType: normativeRuleTypeSchema
        .optional()
        .describe("Optional rule type filter, e.g. min_value."),
      limit: z
        .number()
        .int()
        .positive()
        .max(30)
        .optional()
        .describe(
          "Max rules to return (default 5). Keep small — large payloads slow the agent."
        ),
    },
    async (args) => {
      try {
        const rules = queryNormRules(db, {
          topic: args.topic,
          document: args.document,
          ruleType: args.ruleType,
          limit: args.limit,
        });

        const payload: Record<string, unknown> = {
          success: true,
          count: rules.length,
          rules: compactRulesForMcp(rules),
        };
        if (rules.length === 0) {
          payload.hint =
            "No saved rules match this topic. First retry with synonyms or related terms (Russian and Kazakh). If still empty, extract rules from a normative document with extract_norm_rules_from_pdf, persist them with save_norm_rule (including semantic tags), and retry the query.";
        }

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(payload),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: JSON.stringify({
                success: false,
                error: error instanceof Error ? error.message : String(error),
              }),
            },
          ],
          isError: true,
        };
      }
    }
  );
}
