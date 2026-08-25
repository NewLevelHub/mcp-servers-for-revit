import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { paginateRows } from "../utils/responseTrimming.js";
import { readElementFields } from "../utils/parameterBatch.js";
import { checkCompleteness } from "../quality/dataCompleteness.js";

export function registerCheckDataCompletenessTool(server: McpServer) {
  server.tool(
    "check_data_completeness",
    "REV-181: before assembling a spec/schedule, checks whether the required parameters are actually " +
      "filled in on a set of elements — a leaky spec is usually caught at issue time, and re-doing an " +
      "issued set costs a day where this check costs a minute. Pick the elements first (ai_element_filter, " +
      "get_current_view_elements, a selection) and pass their ids in elementIds. The reply names the " +
      "specific elements and fields that are empty — never just a bare count — plus a byField summary for " +
      "a quick read of which parameter is the actual problem. A whitespace-only value counts as empty, " +
      "same rule fill_parameters_by_rule uses, so the two tools agree on what \"filled in\" means — run " +
      "this first to see what's blank, then fill_parameters_by_rule to fill what a template can reach.",
    {
      elementIds: z
        .array(z.number().int().positive())
        .min(1)
        .describe("Elements to check — already selected by the caller."),
      requiredParameters: z
        .array(z.string().min(1))
        .min(1)
        .describe("Parameter names that must be non-empty, e.g. [\"Марка\", \"Изготовитель\"]. Russian and English both work."),
      offset: z.number().int().min(0).optional().default(0).describe("Rows to skip in the returned element list."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(500)
        .optional()
        .default(100)
        .describe("Max incomplete-element rows returned per call. Use elementsPagination.nextOffset to page through the rest."),
    },
    async (args) => {
      try {
        const { report, readErrors } = await withRevitConnection(async (revitClient) => {
          const { elements, errors } = await readElementFields(revitClient, args.elementIds, args.requiredParameters);
          return { report: checkCompleteness(args.requiredParameters, elements), readErrors: errors };
        });

        const trimmed = paginateRows(
          { elements: report.elements },
          { key: "elements", offset: args.offset ?? 0, limit: args.limit ?? 100, fields: ["all"] }
        );

        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({
                totalChecked: report.totalChecked,
                completeCount: report.completeCount,
                incompleteCount: report.incompleteCount,
                byField: report.byField,
                readErrors: readErrors.length > 0 ? readErrors : undefined,
                message:
                  report.incompleteCount === 0
                    ? `Все ${report.totalChecked} элементов заполнены по требуемым полям.`
                    : `${report.incompleteCount} из ${report.totalChecked} элементов не заполнены хотя бы по одному полю.`,
                ...(trimmed as object),
              }),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `check_data_completeness не выполнен: ${
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
