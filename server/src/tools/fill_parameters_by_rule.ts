import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { paginateRows } from "../utils/responseTrimming.js";
import { chunk, readElementFields, PARAM_BATCH_SIZE } from "../utils/parameterBatch.js";
import { extractTokens, planFill, type FillPlanRow } from "../quality/fillRules.js";

interface SetResultRow {
  success: boolean;
  message: string;
  elementId: number;
  requestedName?: string;
  parameterName?: string;
  newDisplayValue?: string;
}

interface SetBatchResult {
  updatedCount: number;
  failedCount: number;
  results?: SetResultRow[];
}

export function registerFillParametersByRuleTool(server: McpServer) {
  server.tool(
    "fill_parameters_by_rule",
    "REV-181: fills one parameter from a template of other already-set parameters on the same " +
      "elements — e.g. targetParameter «Наименование», template \"{Тип} {Толщина}мм\" turns Тип=Кирпич, " +
      "Толщина=200 into «Кирпич 200мм». Check check_data_completeness first if you don't already know " +
      "which elements/fields are blank — filling a template whose own source field is empty just gets " +
      "skipped and named here, not silently patched. Pick the elements first (ai_element_filter, get_current_view_elements, " +
      "a selection) and pass their ids in elementIds; this tool only reads/writes parameters, it does not " +
      "search for elements. Always previews: with confirm omitted or false, nothing is written — the reply " +
      "shows exactly what would change, per element, so read it before confirming. An element whose target " +
      "parameter already has a non-empty value is left alone unless overwrite:true is also passed — a rule " +
      "run never silently clobbers real data. An element missing one of the template's source parameters is " +
      "skipped and named, not filled with a gap where that piece should be. Batches reads and writes " +
      `internally in groups of ${PARAM_BATCH_SIZE} (get_elements_parameters/set_elements_parameters' own limit), ` +
      "so one call can cover hundreds of elements.",
    {
      elementIds: z
        .array(z.number().int().positive())
        .min(1)
        .describe("Target Revit element ids — the elements to fill, already selected by the caller."),
      template: z
        .string()
        .min(1)
        .describe(
          "Text with {ParameterName} tokens, e.g. \"{Тип} {Толщина}мм\". Tokens are read from each " +
            "element's own parameters — Russian and English names both work, same resolution as " +
            "set_elements_parameters."
        ),
      targetParameter: z.string().min(1).describe("Parameter to write the resolved template into, e.g. «Наименование»."),
      overwrite: z
        .boolean()
        .optional()
        .default(false)
        .describe("If true, replace an existing non-empty targetParameter value too. Default false: only fill blanks."),
      confirm: z
        .boolean()
        .optional()
        .default(false)
        .describe("If true, actually write the planned values. Default false: preview only, nothing is written."),
      offset: z.number().int().min(0).optional().default(0).describe("Rows to skip in the returned plan/report."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(500)
        .optional()
        .default(100)
        .describe("Max rows returned per call. Use rowsPagination.nextOffset to page through the rest."),
    },
    async (args) => {
      try {
        const tokens = extractTokens(args.template);
        const parameterNames = Array.from(new Set([...tokens, args.targetParameter]));

        const { plan, readErrors, writeResult } = await withRevitConnection(async (revitClient) => {
          const { elements, errors } = await readElementFields(revitClient, args.elementIds, parameterNames);

          const planned: FillPlanRow[] = planFill(args.template, args.targetParameter, elements, args.overwrite);

          if (!args.confirm) {
            return { plan: planned, readErrors: errors, writeResult: undefined as SetBatchResult | undefined };
          }

          const toWrite = planned.filter((row) => row.newValue !== undefined);
          const aggregate: SetBatchResult = { updatedCount: 0, failedCount: 0, results: [] };

          for (const batch of chunk(toWrite, PARAM_BATCH_SIZE)) {
            const edits = batch.map((row) => ({
              elementId: row.elementId,
              parameters: { [args.targetParameter]: row.newValue as string },
            }));
            const response = (await revitClient.sendCommand("set_elements_parameters", { edits })) as SetBatchResult;
            aggregate.updatedCount += response.updatedCount ?? 0;
            aggregate.failedCount += response.failedCount ?? 0;
            aggregate.results!.push(...(response.results ?? []));
          }

          return { plan: planned, readErrors: errors, writeResult: aggregate };
        });

        const toWriteCount = plan.filter((r) => r.newValue !== undefined).length;
        const skippedAlreadyHasValue = plan.filter((r) => r.skip === "already-has-value").length;
        const skippedMissingSource = plan.filter((r) => r.skip === "missing-source-field").length;

        const trimmed = paginateRows(
          { rows: plan },
          { key: "rows", offset: args.offset ?? 0, limit: args.limit ?? 100, fields: ["all"] }
        );

        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({
                preview: !args.confirm,
                totalElements: args.elementIds.length,
                plannedWrites: toWriteCount,
                skippedAlreadyHasValue,
                skippedMissingSource,
                readErrors: readErrors.length > 0 ? readErrors : undefined,
                write: writeResult,
                message: args.confirm
                  ? `Записано ${writeResult?.updatedCount ?? 0}, отказов ${writeResult?.failedCount ?? 0}.`
                  : `Предпросмотр: будет заполнено ${toWriteCount} из ${args.elementIds.length}. ` +
                    "Ничего не записано — передайте confirm:true, чтобы применить.",
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
              text: `fill_parameters_by_rule не выполнен: ${
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
