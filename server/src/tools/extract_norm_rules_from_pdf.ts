import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { isAbsolute, resolve } from "node:path";
import db from "../database/db.js";
import { extractRulesFromPdfFile } from "../normatives/extractRulesFromPdf.js";
import {
  compactRulesForMcp,
  getNormLibraryCounts,
  getNormLibraryStats,
  queryNormRules,
  saveNormRules,
  type SaveableNormRule,
  withSuggestedTags,
} from "../normatives/rulesStore.js";
import { seedNormLibrary } from "../normatives/seedLibrary.js";
import {
  normativeRuleSchema,
  normativeRuleTypeSchema,
} from "../normatives/types.js";

/**
 * Flat MCP input for save rules — avoid z.union / nested object schemas that
 * emit anyOf+$ref without $defs (Cursor drops or mishandles those tools).
 * Full shape is validated with normativeRuleSchema inside the handler.
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
    tags: z.array(z.string()).optional(),
  })
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

function resolvePdfPath(inputPath: string): string {
  if (isAbsolute(inputPath)) return inputPath;
  return resolve(process.cwd(), inputPath);
}

type NormRulesAction = "extract" | "save" | "query" | "status" | "seed";

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
  // No args → library status (customer-friendly default)
  return "status";
}

/**
 * Single MCP entry point for norm rules — extract / save / query / status / seed.
 */
export function registerExtractNormRulesFromPdfTool(server: McpServer) {
  server.tool(
    "extract_norm_rules_from_pdf",
    "Norm rules library for ГОСТ/СП/СН РК. Preferred customer flow: (1) action=seed once to load all PDFs from normatives/, (2) pass topic only to search (e.g. «ширина коридора») — returns document/clause/quote. Also: pdfPath to extract one file; saveToLibrary=true to persist; action=status for library document counts. Never invent normative values — use returned quotes.",
    {
      action: z
        .enum(["extract", "save", "query", "status", "seed"])
        .optional()
        .describe(
          "Optional. status=library summary; seed=ingest all normatives/*.pdf; topic without pdfPath→query; pdfPath→extract; rules→save. Empty call → status."
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
        .array(looseSaveRuleSchema)
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
        .describe("Optional max PDF pages to parse (extract/seed)."),
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
      limit: z
        .number()
        .int()
        .positive()
        .max(30)
        .optional()
        .describe(
          "Max rules to return for query (default 5). Keep small — large payloads slow the agent."
        ),
      metadata: z
        .object({
          mode: z.enum(["embedded-text", "ocr"]).optional(),
          confidence: z.number().min(0).max(1).optional(),
          extractedAt: z.string().optional(),
        })
        .optional(),
    },
    async (args) => {
      try {
        const action = resolveNormRulesAction(args);

        if (action === "status") {
          const library = getNormLibraryStats(db);
          return {
            content: [
              {
                type: "text",
                text: JSON.stringify(
                  {
                    success: true,
                    action: "status",
                    library,
                    hint:
                      library.ruleCount === 0
                        ? "Library empty. Call action=seed to load normatives/*.pdf, or extract one PDF with saveToLibrary=true."
                        : "Search with topic only, e.g. topic «ширина коридора».",
                  },
                  null,
                  2
                ),
              },
            ],
          };
        }

        if (action === "seed") {
          const result = await seedNormLibrary(db, {
            maxPages: args.maxPages ?? 60,
          });
          return {
            content: [
              {
                type: "text",
                text: JSON.stringify(
                  {
                    success: true,
                    action: "seed",
                    ...result,
                    nextStep:
                      "Search by topic only, e.g. topic «ширина коридора» or «основная надпись».",
                  },
                  null,
                  2
                ),
              },
            ],
          };
        }

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
          const library = getNormLibraryCounts(db);
          const payload: Record<string, unknown> = {
            success: true,
            action: "query",
            count: rules.length,
            rules: compactRulesForMcp(rules),
            library,
          };
          if (rules.length === 0) {
            payload.hint =
              library.ruleCount === 0
                ? "Library empty. Run action=seed first, then retry the topic."
                : "No saved rules match. Retry with synonyms (ru/kz) or extract a PDF with saveToLibrary=true.";
          }
          return {
            // Compact JSON (no pretty-print) — less tokens / faster for the agent.
            content: [{ type: "text", text: JSON.stringify(payload) }],
          };
        }

        if (action === "save") {
          if (!args.rules?.length) {
            throw new Error("Save requires rules array from a prior extract.");
          }
          const rules = parseSaveableRules(args.rules);
          const result = saveNormRules(db, withSuggestedTags(rules), {
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
          throw new Error(
            "Extract requires pdfPath, or use action=seed / action=status / topic for query."
          );
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
                    : "Re-run with saveToLibrary=true, or action=seed for all PDFs.",
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
