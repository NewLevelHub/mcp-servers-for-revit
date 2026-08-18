import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { SHEET_NUMBER_ALIASES } from "../utils/titleBlock.js";
import {
  fetchParametersBatch,
  findParamValue,
  parseFilteredElements,
} from "../utils/revitElementQuery.js";
import {
  REQUIRED_SHEET_FIELDS,
  buildReadinessReport,
  summarizeReadiness,
  type SheetInput,
} from "../utils/sheetReadiness.js";
import { paginateRows } from "../utils/responseTrimming.js";

export function registerCheckSheetReadinessTool(server: McpServer) {
  server.tool(
    "check_sheet_readiness",
    "Pre-issue check of the drawing set: which sheets still have a blank штамп line, no number, a duplicate " +
      "number, or no name. Answers «готов ли комплект к выдаче» before the set goes out. Read-only — it reports, " +
      "it does not fill anything in; hand the result to fill_title_block to write the missing names. " +
      "Checks the sheets themselves; for what Revit flagged in the model use get_model_warnings, and for " +
      "СП/ГОСТ compliance use run_norm_audit.",
    {
      fields: z
        .array(z.enum(["drawnBy", "checkedBy", "chiefEngineer", "normControl", "issueDate", "totalSheets"]))
        .optional()
        .describe(
          `Which штамп lines must be filled. Default ${JSON.stringify([...REQUIRED_SHEET_FIELDS])} — ` +
            "the four signatures. Add issueDate / totalSheets when the client requires them."
        ),
      onlyProblems: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Return only sheets with issues (default). false lists every sheet, including the ready ones."
        ),
      offset: z
        .number()
        .int()
        .min(0)
        .optional()
        .default(0)
        .describe("Sheets to skip. Use sheetsPagination.nextOffset from the previous call."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(500)
        .optional()
        .default(100)
        .describe("Max sheets per call (default 100). The summary always covers the whole set."),
    },
    async (args) => {
      try {
        const report = await withRevitConnection(async (revitClient) => {
          const sheetsResponse = await revitClient.sendCommand("ai_element_filter", {
            data: {
              filterCategory: "OST_Sheets",
              includeTypes: false,
              includeInstances: true,
            },
          });

          const sheetElements = parseFilteredElements(sheetsResponse);
          if (sheetElements.length === 0) {
            return {
              success: true,
              message: "В проекте нет листов (OST_Sheets) — проверять нечего.",
              summary: {
                totalSheets: 0,
                readySheets: 0,
                sheetsWithIssues: 0,
                duplicateNumbers: [],
                blankFieldCounts: [],
              },
              sheets: [],
            };
          }

          const paramsBySheet = await fetchParametersBatch(
            revitClient,
            sheetElements.map((sheet) => sheet.id)
          );

          const sheets: SheetInput[] = sheetElements.map((sheet) => {
            const parameters = paramsBySheet.get(sheet.id)?.parameters ?? [];
            return {
              id: sheet.id,
              name: sheet.name,
              number: findParamValue(parameters, SHEET_NUMBER_ALIASES),
              parameters,
            };
          });

          // A sheet whose parameters could not be read would otherwise be graded
          // as "every field blank" — a confident wrong answer about someone's work.
          const unreadable = sheets.filter(
            (sheet) => (paramsBySheet.get(sheet.id)?.parameters ?? []).length === 0
          );

          const graded = buildReadinessReport(
            sheets.filter((sheet) => !unreadable.includes(sheet)),
            args.fields ?? REQUIRED_SHEET_FIELDS
          );

          return {
            success: true,
            message: summarizeReadiness(graded.summary),
            summary: graded.summary,
            sheets: args.onlyProblems === false
              ? graded.sheets
              : graded.sheets.filter((sheet) => !sheet.ready),
            ...(unreadable.length > 0
              ? {
                  unreadableSheets: {
                    count: unreadable.length,
                    ids: unreadable.slice(0, 20).map((sheet) => sheet.id),
                    note:
                      "У этих листов не удалось прочитать параметры, поэтому они не оценивались. " +
                      "Проверь их вручную.",
                  },
                }
              : {}),
          };
        });

        const trimmed = paginateRows(report, {
          key: "sheets",
          offset: args.offset ?? 0,
          limit: args.limit ?? 100,
          fields: ["all"],
        });

        return {
          content: [{ type: "text" as const, text: JSON.stringify(trimmed) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `check_sheet_readiness не выполнен: ${
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
