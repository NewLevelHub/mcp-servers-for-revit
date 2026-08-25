import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { isAbsolute, resolve } from "node:path";
import db from "../database/db.js";
import { extractRequirementsFromFile } from "../projectBrief/extractRequirementsFromFile.js";
import {
  getBriefLibraryStats,
  queryBriefRequirements,
  saveBriefRequirements,
} from "../projectBrief/briefStore.js";
import { briefRequirementSchema, briefRequirementTypeSchema } from "../projectBrief/types.js";

/** Flat, no unions — same reasoning as extract_norm_rules_from_pdf's looseSaveRuleSchema. */
const looseSaveRequirementSchema = z
  .object({
    id: z.string(),
    type: z.string(),
    object: z.string(),
    value: z.union([z.number(), z.string()]),
    unit: z.string(),
    source: z.object({
      document: z.string(),
      clause: z.string(),
      quote: z.string(),
      page: z.number().optional(),
    }),
  })
  .passthrough();

function parseSaveableRequirements(raw: unknown[]) {
  return raw.map((item, index) => {
    const parsed = briefRequirementSchema.safeParse(item);
    if (!parsed.success) {
      throw new Error(
        `requirements[${index}] invalid: ${parsed.error.issues.map((i) => i.message).join("; ")}`
      );
    }
    return parsed.data;
  });
}

function resolveFilePath(inputPath: string): string {
  if (isAbsolute(inputPath)) return inputPath;
  return resolve(process.cwd(), inputPath);
}

type BriefAction = "extract" | "save" | "query" | "status";

function resolveAction(args: {
  action?: BriefAction;
  topic?: string;
  filePath?: string;
  requirements?: unknown[];
}): BriefAction {
  if (args.action) return args.action;
  if (args.requirements?.length) return "save";
  if (args.filePath) return "extract";
  if (args.topic) return "query";
  return "status";
}

export function registerQueryProjectBriefTool(server: McpServer) {
  server.tool(
    "query_project_brief",
    "REV-182: project brief library (ТЗ / задание на проектирование / протоколы совещаний) — same shape " +
      "as extract_norm_rules_from_pdf, but for THIS project's own client requirements instead of the law. " +
      "Flow: filePath (.pdf/.docx) to extract, saveToLibrary:true to persist, then topic to search (e.g. " +
      "«состав помещений», «количество студий») — returns the matching requirement with its exact quote and " +
      "clause. Extraction is heuristic and was built without a real project ТЗ to test against (see " +
      "projectBrief/extractRequirements.ts) — expect it to miss unusual phrasings, and NEVER invent a " +
      "requirement the search didn't return; when nothing matches, say the brief doesn't cover it rather than " +
      "guessing. For a numeric room-count/area check against the model, use check_against_brief instead of " +
      "reading these quotes by hand.",
    {
      action: z
        .enum(["extract", "save", "query", "status"])
        .optional()
        .describe(
          "Optional. status=library summary; filePath→extract; requirements→save; topic (no filePath)→query. Empty call → status."
        ),
      filePath: z
        .string()
        .min(1)
        .optional()
        .describe("Path to a .pdf or .docx project brief to extract. Omit when searching by topic."),
      topic: z
        .string()
        .min(1)
        .optional()
        .describe("Search the saved library by topic, e.g. «количество студий», «площадь кладовых»."),
      requirements: z
        .array(looseSaveRequirementSchema)
        .min(1)
        .optional()
        .describe("Required for action=save — requirements from a prior extract call."),
      document: z
        .string()
        .optional()
        .describe("Document title override for extract, or a document filter for query."),
      clauseHint: z.string().optional().describe("Optional clause/section hint when extracting one paragraph."),
      page: z.number().int().positive().optional(),
      maxPages: z.number().int().positive().optional().describe("Optional max PDF pages to parse."),
      saveToLibrary: z.boolean().optional().describe("When true on extract, saves requirements to SQLite immediately."),
      documentVersion: z.string().optional().describe("Document edition/date, e.g. 12.03.2026."),
      type: briefRequirementTypeSchema.optional().describe("Optional requirement type filter for query."),
      limit: z
        .number()
        .int()
        .positive()
        .max(50)
        .optional()
        .describe("Max requirements to return for query (default 10)."),
    },
    async (args) => {
      try {
        const action = resolveAction(args);

        if (action === "status") {
          const library = getBriefLibraryStats(db);
          return {
            content: [
              {
                type: "text",
                text: JSON.stringify({
                  success: true,
                  action: "status",
                  library,
                  hint:
                    library.requirementCount === 0
                      ? "Library empty. Extract a brief with filePath, then saveToLibrary=true."
                      : "Search with topic only, e.g. topic «количество студий».",
                }),
              },
            ],
          };
        }

        if (action === "query") {
          if (!args.topic) throw new Error("Search requires topic (e.g. «количество студий»).");
          const requirements = queryBriefRequirements(db, {
            topic: args.topic,
            document: args.document,
            type: args.type,
            limit: args.limit,
          });
          const library = getBriefLibraryStats(db);
          const payload: Record<string, unknown> = {
            success: true,
            action: "query",
            count: requirements.length,
            requirements,
            library,
          };
          if (requirements.length === 0) {
            payload.hint =
              library.requirementCount === 0
                ? "Library empty. Extract a brief first (filePath, saveToLibrary=true)."
                : "В задании этого не нашлось — не изобретай ответ. Попробуй другой topic или проверь исходный документ.";
          }
          return { content: [{ type: "text", text: JSON.stringify(payload) }] };
        }

        if (action === "save") {
          if (!args.requirements?.length) throw new Error("Save requires requirements array from a prior extract.");
          const requirements = parseSaveableRequirements(args.requirements);
          const result = saveBriefRequirements(db, requirements, { documentVersion: args.documentVersion });
          return {
            content: [{ type: "text", text: JSON.stringify({ success: true, action: "save", ...result }) }],
          };
        }

        if (!args.filePath) {
          throw new Error("Extract requires filePath (.pdf/.docx), or use action=status / topic for query.");
        }

        const filePath = resolveFilePath(args.filePath);
        const result = await extractRequirementsFromFile({
          filePath,
          document: args.document,
          clauseHint: args.clauseHint,
          page: args.page,
          maxPages: args.maxPages,
        });

        let saved: { inserted: number; updated: number } | undefined;
        if (args.saveToLibrary) {
          const saveResult = saveBriefRequirements(db, result.requirements, {
            documentVersion: args.documentVersion,
          });
          saved = { inserted: saveResult.inserted, updated: saveResult.updated };
        }

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify({
                success: true,
                action: "extract",
                filePath,
                requirementCount: result.requirements.length,
                saved,
                nextStep: args.saveToLibrary
                  ? "Search by topic only, e.g. topic «количество студий»."
                  : "Re-run with saveToLibrary=true to persist these requirements.",
                ...result,
              }),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `query_project_brief failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
