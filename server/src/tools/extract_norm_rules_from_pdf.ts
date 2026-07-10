import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { isAbsolute, resolve } from "node:path";
import db from "../database/db.js";
import { extractRulesFromPdfFile } from "../normatives/extractRulesFromPdf.js";
import {
  queryNormRules,
  saveNormRules,
  withSuggestedTags,
} from "../normatives/rulesStore.js";
import {
  normativeApplicabilitySchema,
  normativeNumericRangeSchema,
  normativeRuleTypeSchema,
  normativeSourceRefSchema,
  normativeUnitSchema,
} from "../normatives/types.js";

const saveRuleInputSchema = z.object({
  id: z.string(),
  type: normativeRuleTypeSchema,
  object: z.string(),
  value: z.union([z.number(), z.string(), normativeNumericRangeSchema]),
  unit: normativeUnitSchema,
  applicability: normativeApplicabilitySchema.nullable(),
  source: normativeSourceRefSchema,
  normalized: normativeNumericRangeSchema.optional(),
  tags: z.array(z.string()).optional(),
});

function resolvePdfPath(inputPath: string): string {
  if (isAbsolute(inputPath)) return inputPath;
  return resolve(process.cwd(), inputPath);
}

type NormRulesAction = "extract" | "save" | "query";

/** Infer mode from parameters — user/agent need not pass action explicitly. */
function resolveNormRulesAction(args: {
  action?: NormRulesAction;
  topic?: string;
  pdfPath?: string;
  rules?: unknown[];
}): NormRulesAction {
  if (args.action) return args.action;
  if (args.rules?.length) return "save";
  if (args.topic && !args.pdfPath) return "query";
  if (args.pdfPath) return "extract";
  if (args.topic) return "query";
  return "extract";
}

/**
 * Single MCP entry point for norm rules — extract / save / query.
 * Cursor may not expose save_norm_rule and query_norm_rules to the agent;
 * use the action parameter on this tool instead.
 */
export function registerExtractNormRulesFromPdfTool(server: McpServer) {
  server.tool(
    "extract_norm_rules_from_pdf",
    "Norm rules library (one tool for all operations). Search saved rules: pass topic only (e.g. topic «основная надпись») — no pdfPath needed. Extract from PDF: pass pdfPath; add saveToLibrary=true to persist. Save rules array: pass rules. The action field is optional — mode is inferred from topic/pdfPath/rules.",
    {
      action: z
        .enum(["extract", "save", "query"])
        .optional()
        .describe(
          "Optional. Inferred automatically: topic without pdfPath → query; pdfPath → extract; rules → save."
        ),
      pdfPath: z
        .string()
        .min(1)
        .optional()
        .describe(
          "PDF path for extraction, e.g. ../normatives/ГОСТ-21.101-97-....pdf. Omit when searching by topic."
        ),
      topic: z
        .string()
        .min(1)
        .optional()
        .describe(
          "Search the saved library by topic, e.g. «основная надпись», «ширина коридора». Use without pdfPath."
        ),
      rules: z
        .array(saveRuleInputSchema)
        .min(1)
        .optional()
        .describe("Required for action=save — rules from a prior extract call."),
      document: z
        .string()
        .optional()
        .describe("Optional document title/code override, e.g. СП РК 3.02-101."),
      clauseHint: z
        .string()
        .optional()
        .describe("Optional clause hint when extracting a specific paragraph."),
      page: z.number().int().positive().optional().describe("Optional source page."),
      maxPages: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("Optional max PDF pages to parse (extract only)."),
      saveToLibrary: z
        .boolean()
        .optional()
        .describe("When true on extract, saves rules to SQLite immediately."),
      documentVersion: z
        .string()
        .optional()
        .describe("Document edition, e.g. 97 or 27.04.2021 (save / extract+save)."),
      ruleType: normativeRuleTypeSchema
        .optional()
        .describe("Optional filter for action=query."),
      limit: z.number().int().positive().max(200).optional(),
      metadata: z
        .object({
          mode: z.enum(["embedded-text", "ocr"]).optional(),
          confidence: z.number().min(0).max(1).optional(),
          extractedAt: z.string().datetime().optional(),
        })
        .optional(),
    },
    async (args) => {
      try {
        const action = resolveNormRulesAction(args);

        if (action === "query") {
          if (!args.topic) {
            throw new Error("Search requires topic (e.g. «основная надпись»).");
          }
          const rules = queryNormRules(db, {
            topic: args.topic,
            document: args.document,
            ruleType: args.ruleType,
            limit: args.limit,
          });
          const payload: Record<string, unknown> = {
            success: true,
            action: "query",
            count: rules.length,
            rules,
          };
          if (rules.length === 0) {
            payload.hint =
              "No saved rules match. Retry with synonyms (ru/kz) or extract a PDF with saveToLibrary=true.";
          }
          return {
            content: [{ type: "text", text: JSON.stringify(payload, null, 2) }],
          };
        }

        if (action === "save") {
          if (!args.rules?.length) {
            throw new Error("Save requires rules array from a prior extract.");
          }
          const result = saveNormRules(db, withSuggestedTags(args.rules), {
            documentVersion: args.documentVersion,
          });
          return {
            content: [
              {
                type: "text",
                text: JSON.stringify(
                  { success: true, action: "save", ...result },
                  null,
                  2
                ),
              },
            ],
          };
        }

        if (!args.pdfPath) {
          throw new Error("Extract requires pdfPath to a normative PDF.");
        }

        const pdfPath = resolvePdfPath(args.pdfPath);
        const result = await extractRulesFromPdfFile({
          pdfPath,
          document: args.document,
          clauseHint: args.clauseHint,
          page: args.page,
          maxPages: args.maxPages,
          metadata: args.metadata,
        });

        let saved: { inserted: number; updated: number } | undefined;

        if (args.saveToLibrary) {
          const saveResult = saveNormRules(
            db,
            withSuggestedTags(result.rules),
            { documentVersion: args.documentVersion }
          );
          saved = {
            inserted: saveResult.inserted,
            updated: saveResult.updated,
          };
        }

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(
                {
                  success: true,
                  action: "extract",
                  pdfPath,
                  ruleCount: result.rules.length,
                  saved,
                  nextStep: args.saveToLibrary
                    ? "Search by topic only, e.g. topic «основная надпись»."
                    : "Re-run with saveToLibrary=true.",
                  ...result,
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
              text: `extract_norm_rules_from_pdf failed: ${
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
