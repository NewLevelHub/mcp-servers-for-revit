import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import db from "../database/db.js";
import { normativeRuleSchema } from "../normatives/types.js";
import {
  saveNormRules,
  type SaveableNormRule,
  withSuggestedTags,
} from "../normatives/rulesStore.js";

/**
 * Flat MCP input — avoid normativeRuleSchema unions that emit anyOf+$ref
 * (Cursor may drop tools with those schemas). Validate inside the handler.
 */
const looseSaveRuleSchema = z
  .object({
    id: z.string(),
    type: z.string(),
    object: z.string(),
    value: z.any(),
    unit: z.string(),
    source: z.object({
      document: z.string(),
      clause: z.string(),
      quote: z.string(),
      page: z.number().optional(),
    }),
    tags: z
      .array(z.string())
      .optional()
      .describe(
        "3-8 semantic tags: synonyms and related terms in Russian AND Kazakh. Generate them yourself — they power topic search."
      ),
  })
  // Keep applicability/normalized without emitting anyOf+null in MCP JSON Schema.
  .passthrough();

function parseSaveableRules(raw: unknown[]): SaveableNormRule[] {
  return raw.map((item, index) => {
    const parsed = normativeRuleSchema
      .extend({ tags: z.array(z.string()).optional() })
      .safeParse(item);
    if (!parsed.success) {
      throw new Error(
        `rules[${index}] invalid: ${parsed.error.issues
          .map((i) => i.message)
          .join("; ")}`
      );
    }
    return parsed.data;
  });
}

export function registerSaveNormRuleTool(server: McpServer) {
  server.tool(
    "save_norm_rule",
    "Persist extracted normative rules into the local rules library (SQLite) so they survive between sessions. ALWAYS call this after extract_norm_rules_from_pdf — otherwise rules are lost when the session ends. Rules are deduplicated by document+clause+object+type; saving an existing rule updates it. For EACH rule, generate 3-8 semantic tags: synonyms and related terms in BOTH Russian and Kazakh (e.g. for a corridor width rule: коридор, ширина коридора, проход, путь эвакуации, дәліз, дәліз ені). Tags are what makes query_norm_rules find rules by meaning.",
    {
      rules: z
        .array(looseSaveRuleSchema)
        .min(1)
        .describe(
          "Rules to save, in the format returned by extract_norm_rules_from_pdf, each enriched with semantic tags."
        ),
      documentVersion: z
        .string()
        .optional()
        .describe(
          "Document edition/revision, e.g. 27.04.2021. Applied to all rules in this call."
        ),
    },
    async (args) => {
      try {
        const rules = parseSaveableRules(args.rules);
        const result = saveNormRules(db, withSuggestedTags(rules), {
          documentVersion: args.documentVersion,
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(
                {
                  success: true,
                  inserted: result.inserted,
                  updated: result.updated,
                  results: result.results,
                },
                null,
                2
              ),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `save_norm_rule failed: ${
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
