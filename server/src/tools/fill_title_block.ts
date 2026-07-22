import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { RevitClientConnection } from "../utils/SocketClient.js";
import {
  buildAutoNumberPlan,
  chunk,
  PROJECT_FIELD_ALIASES,
  resolveDateVisibilityParameterNames,
  resolveParameterName,
  SHEET_FIELD_ALIASES,
  SHEET_NUMBER_ALIASES,
  TOTAL_SHEETS_VISIBILITY_ALIASES,
  type AvailableParameter,
} from "../utils/titleBlock.js";

const BATCH_SIZE = 20;

interface SheetInfo {
  id: number;
  name: string;
  number: string;
  parameters: AvailableParameter[];
}

interface TitleBlockInfo {
  id: number;
  sheetNumber: string;
  parameters: AvailableParameter[];
}

interface WriteOp {
  elementId: number;
  parameterName: string;
  value: string | number | boolean;
  /** For the report: sheet number / «project» the write belongs to. */
  target: string;
}

const elementListSchema = z.object({
  elements: z
    .array(z.object({ id: z.number(), name: z.string().optional().default("") }))
    .optional(),
});

function parseFilteredElements(response: unknown): Array<{ id: number; name: string }> {
  if (Array.isArray(response)) {
    return response
      .filter((item) => item && typeof item === "object")
      .map((item) => ({
        id: Number((item as Record<string, unknown>).id ?? (item as Record<string, unknown>).Id),
        name: String(
          (item as Record<string, unknown>).name ?? (item as Record<string, unknown>).Name ?? ""
        ),
      }))
      .filter((item) => Number.isFinite(item.id));
  }
  if (response && typeof response === "object") {
    const parsed = elementListSchema.safeParse(response);
    if (parsed.success && parsed.data.elements) {
      return parsed.data.elements.map((element) => ({
        id: element.id,
        name: element.name,
      }));
    }
    // Some responses wrap the list under a different key — take the first array of objects with ids.
    for (const value of Object.values(response as Record<string, unknown>)) {
      if (Array.isArray(value) && value.length > 0 && typeof value[0] === "object") {
        return parseFilteredElements(value);
      }
    }
  }
  return [];
}

const parametersResponseSchema = z.object({
  success: z.boolean().optional(),
  elementId: z.number().optional(),
  parameters: z
    .array(
      z.object({
        name: z.string(),
        displayValue: z.string().optional().default(""),
        isReadOnly: z.boolean().optional().default(false),
      })
    )
    .optional()
    .default([]),
});

async function fetchParametersBatch(
  revitClient: RevitClientConnection,
  elementIds: number[]
): Promise<Map<number, z.infer<typeof parametersResponseSchema>>> {
  const byId = new Map<number, z.infer<typeof parametersResponseSchema>>();
  for (const ids of chunk(elementIds, BATCH_SIZE)) {
    const response = await revitClient.sendCommand("get_elements_parameters", {
      elementIds: ids,
      slim: true,
    });
    const batch = z
      .object({
        success: z.boolean().optional(),
        results: z.array(parametersResponseSchema).optional().default([]),
      })
      .safeParse(response);
    if (!batch.success) continue;
    for (const item of batch.data.results) {
      if (item.elementId != null) {
        byId.set(item.elementId, item);
      }
    }
  }
  return byId;
}

function findParamValue(
  parameters: Array<{ name: string; displayValue?: string }>,
  aliases: readonly string[]
): string {
  for (const alias of aliases) {
    const match = parameters.find(
      (parameter) => parameter.name.toLowerCase() === alias.toLowerCase()
    );
    if (match) return match.displayValue ?? "";
  }
  return "";
}

async function executeWrites(
  revitClient: RevitClientConnection,
  writes: WriteOp[]
): Promise<{ succeeded: number; failures: string[] }> {
  let succeeded = 0;
  const failures: string[] = [];
  for (const ops of chunk(writes, BATCH_SIZE)) {
    const response = await revitClient.sendCommand("batch_execute", {
      commands: ops.map((op) => ({
        command: "set_element_parameter",
        params: {
          elementId: op.elementId,
          parameterName: op.parameterName,
          value: op.value,
        },
      })),
    });
    const results = (response as { results?: Array<Record<string, unknown>> })?.results ?? [];
    results.forEach((item, index) => {
      const op = ops[index];
      const resultSuccess =
        item.success === true &&
        (item.result as Record<string, unknown> | undefined)?.success !== false;
      if (resultSuccess) {
        succeeded += 1;
      } else {
        const error =
          ((item.result as Record<string, unknown> | undefined)?.message as string) ??
          ((item.error as Record<string, unknown> | undefined)?.message as string) ??
          "unknown error";
        failures.push(`${op.target} · ${op.parameterName}: ${error}`);
      }
    });
  }
  return { succeeded, failures };
}

export function registerFillTitleBlockTool(server: McpServer) {
  server.tool(
    "fill_title_block",
    "Автозаполнение основной надписи (штамп СПДС/ГОСТ 21.501) на листах. Batch-fills title block fields " +
      "using ONLY the project's existing frame families — no new families are created. Project-wide fields " +
      "(шифр/Project Number, наименование, стадия/Project Status, заказчик) are written to Project Information, " +
      "which frame labels read automatically. Per-sheet fields (разработал, проверил, ГИП, н.контроль, дата) " +
      "are written to every sheet in scope; parameter names are resolved from RU/EN/ADSK aliases against the " +
      "project template, unresolved fields are reported, not silently skipped. autoNumber renumbers sheets " +
      "sequentially (natural order of current numbers, optional prefix/zero-padding, collision-safe two-pass) " +
      "and «Листов» is filled with the sheet count when the parameter exists. Use dryRun=true to preview the " +
      "write plan without touching the model. Scale is NOT written — it is view-driven in Revit.",
    {
      projectFields: z
        .object({
          code: z.string().optional().describe("Шифр проекта → Номер проекта / Project Number"),
          name: z.string().optional().describe("Наименование проекта / Project Name"),
          stage: z.string().optional().describe("Стадия → Статус проекта / Project Status, e.g. «Р»"),
          client: z.string().optional().describe("Заказчик / Client Name"),
          building: z.string().optional().describe("Наименование здания/объекта"),
        })
        .optional()
        .describe("Values written once to Project Information (штамп reads them on every sheet)."),
      sheetFields: z
        .object({
          drawnBy: z.string().optional().describe("Разработал / Drawn By"),
          checkedBy: z.string().optional().describe("Проверил / Checked By"),
          chiefEngineer: z.string().optional().describe("ГИП / Утвердил / Approved By"),
          normControl: z.string().optional().describe("Н.контроль"),
          issueDate: z.string().optional().describe("Дата, e.g. «07.2026»"),
        })
        .optional()
        .describe("Values written to every sheet in scope."),
      customSheetFields: z
        .record(z.string())
        .optional()
        .describe(
          "Exact sheet parameter name → value, for организация-specific штамп params not covered by aliases."
        ),
      autoNumber: z
        .object({
          startNumber: z.number().int().min(1).optional().default(1),
          prefix: z.string().optional().default("").describe("e.g. «АР-»"),
          padWidth: z
            .number()
            .int()
            .min(0)
            .max(4)
            .optional()
            .default(0)
            .describe("Zero-pad width: 2 → «01, 02…»"),
        })
        .optional()
        .describe("Renumber sheets sequentially in natural order of current numbers."),
      fillTotalSheets: z
        .boolean()
        .optional()
        .default(true)
        .describe("Fill «Листов» (total sheet count) on every sheet when the parameter exists."),
      sheetNumberPrefix: z
        .string()
        .optional()
        .describe("Only process sheets whose current number starts with this prefix, e.g. «АР»."),
      sheetNumbers: z
        .array(z.string())
        .optional()
        .describe("Only process sheets with these current numbers."),
      dryRun: z
        .boolean()
        .optional()
        .default(false)
        .describe("Preview the write plan without modifying the model."),
    },
    async (args) => {
      try {
        const warnings: string[] = [];

        const result = await withRevitConnection(async (revitClient) => {
          // 1. Sheets in the project
          const sheetsResponse = await revitClient.sendCommand("ai_element_filter", {
            data: {
              filterCategory: "OST_Sheets",
              includeTypes: false,
              includeInstances: true,
            },
          });
          const sheetElements = parseFilteredElements(sheetsResponse);
          if (sheetElements.length === 0) {
            throw new Error("В проекте не найдено листов (OST_Sheets).");
          }

          // 2. Sheet parameters (numbers + available штамп params)
          const paramsBySheet = await fetchParametersBatch(
            revitClient,
            sheetElements.map((sheet) => sheet.id)
          );

          let sheets: SheetInfo[] = sheetElements.map((sheet) => {
            const data = paramsBySheet.get(sheet.id);
            const parameters = data?.parameters ?? [];
            return {
              id: sheet.id,
              name: sheet.name,
              number: findParamValue(parameters, SHEET_NUMBER_ALIASES),
              parameters,
            };
          });

          const missingParams = sheets.filter((sheet) => sheet.parameters.length === 0);
          if (missingParams.length > 0) {
            warnings.push(
              `Не удалось прочитать параметры у ${missingParams.length} листов — они пропущены.`
            );
            sheets = sheets.filter((sheet) => sheet.parameters.length > 0);
          }

          // 3. Scope filter
          if (args.sheetNumbers && args.sheetNumbers.length > 0) {
            const wanted = new Set(args.sheetNumbers.map((n) => n.trim()));
            sheets = sheets.filter((sheet) => wanted.has(sheet.number));
          } else if (args.sheetNumberPrefix) {
            sheets = sheets.filter((sheet) =>
              sheet.number.startsWith(args.sheetNumberPrefix!)
            );
          }
          if (sheets.length === 0) {
            throw new Error("После фильтра по номерам не осталось ни одного листа.");
          }

          // Frame-instance visibility controls are separate from sheet data in
          // common ADSK families. Map existing title blocks back to their sheet
          // number so date/«Листов» values are not merely written but also shown.
          const titleBlocksBySheetNumber = new Map<string, TitleBlockInfo[]>();
          const needsTitleBlockVisibility =
            Boolean(args.sheetFields?.issueDate) || args.fillTotalSheets !== false;
          if (needsTitleBlockVisibility) {
            const titleBlocksResponse = await revitClient.sendCommand("ai_element_filter", {
              data: {
                filterCategory: "OST_TitleBlocks",
                includeTypes: false,
                includeInstances: true,
              },
            });
            const titleBlockElements = parseFilteredElements(titleBlocksResponse);
            const paramsByTitleBlock = await fetchParametersBatch(
              revitClient,
              titleBlockElements.map((titleBlock) => titleBlock.id)
            );
            for (const titleBlock of titleBlockElements) {
              const parameters = paramsByTitleBlock.get(titleBlock.id)?.parameters ?? [];
              const sheetNumber = findParamValue(parameters, SHEET_NUMBER_ALIASES);
              if (!sheetNumber) continue;
              const existing = titleBlocksBySheetNumber.get(sheetNumber) ?? [];
              existing.push({ id: titleBlock.id, sheetNumber, parameters });
              titleBlocksBySheetNumber.set(sheetNumber, existing);
            }
          }

          const referenceParams = sheets[0].parameters;
          const writes: WriteOp[] = [];
          const unresolvedFields: string[] = [];

          // 4. Project Information fields
          const projectEntries = Object.entries(args.projectFields ?? {}).filter(
            ([, value]) => value != null && value !== ""
          );
          if (projectEntries.length > 0) {
            const projectInfoResponse = await revitClient.sendCommand("ai_element_filter", {
              data: {
                filterCategory: "OST_ProjectInformation",
                includeTypes: false,
                includeInstances: true,
              },
            });
            const projectInfo = parseFilteredElements(projectInfoResponse)[0];
            if (!projectInfo) {
              warnings.push(
                "Элемент «Сведения о проекте» не найден — поля проекта не записаны."
              );
            } else {
              const projectParamsById = await fetchParametersBatch(revitClient, [
                projectInfo.id,
              ]);
              const projectParams = projectParamsById.get(projectInfo.id)?.parameters ?? [];
              for (const [key, value] of projectEntries) {
                const aliases = PROJECT_FIELD_ALIASES[key];
                const resolved = resolveParameterName(projectParams, aliases ?? []);
                if (!resolved.name) {
                  unresolvedFields.push(
                    `project.${key} (пробовали: ${(aliases ?? []).join(", ")})` +
                      (resolved.readOnlyMatch
                        ? ` — найден только read-only «${resolved.readOnlyMatch}»`
                        : "")
                  );
                  continue;
                }
                writes.push({
                  elementId: projectInfo.id,
                  parameterName: resolved.name,
                  value: value as string,
                  target: "project",
                });
              }
            }
          }

          // 5. Per-sheet fields (aliases resolved once against the reference sheet)
          const sheetEntries = Object.entries(args.sheetFields ?? {}).filter(
            ([, value]) => value != null && value !== ""
          );
          const configuredDateRows = (
            [
              ["chiefEngineer", 2],
              ["drawnBy", 4],
              ["checkedBy", 5],
              ["normControl", 6],
            ] as const
          )
            .filter(([key]) => Boolean(args.sheetFields?.[key]))
            .map(([, row]) => row);
          const dateRows =
            configuredDateRows.length > 0 ? configuredDateRows : [2, 4, 5, 6];
          for (const [key, value] of sheetEntries) {
            const aliases = SHEET_FIELD_ALIASES[key];
            const resolved = resolveParameterName(referenceParams, aliases ?? []);
            if (!resolved.name) {
              unresolvedFields.push(
                `sheet.${key} (пробовали: ${(aliases ?? []).join(", ")})` +
                  (resolved.readOnlyMatch
                    ? ` — найден только read-only «${resolved.readOnlyMatch}»`
                    : "")
              );
              continue;
            }
            for (const sheet of sheets) {
              writes.push({
                elementId: sheet.id,
                parameterName: resolved.name,
                value: value as string,
                target: sheet.number || sheet.name,
              });
            }
            if (key === "issueDate") {
              for (const sheet of sheets) {
                for (const titleBlock of titleBlocksBySheetNumber.get(sheet.number) ?? []) {
                  for (const parameterName of resolveDateVisibilityParameterNames(
                    titleBlock.parameters,
                    dateRows
                  )) {
                    writes.push({
                      elementId: titleBlock.id,
                      parameterName,
                      value: true,
                      target: `${sheet.number || sheet.name} · рамка`,
                    });
                  }
                }
              }
            }
          }

          for (const [parameterName, value] of Object.entries(args.customSheetFields ?? {})) {
            for (const sheet of sheets) {
              writes.push({
                elementId: sheet.id,
                parameterName,
                value,
                target: sheet.number || sheet.name,
              });
            }
          }

          // 6. «Листов» — total sheet count
          if (args.fillTotalSheets !== false) {
            const resolved = resolveParameterName(
              referenceParams,
              SHEET_FIELD_ALIASES.totalSheets
            );
            if (resolved.name) {
              for (const sheet of sheets) {
                writes.push({
                  elementId: sheet.id,
                  parameterName: resolved.name,
                  value: String(sheets.length),
                  target: sheet.number || sheet.name,
                });
                for (const titleBlock of titleBlocksBySheetNumber.get(sheet.number) ?? []) {
                  const visibility = resolveParameterName(
                    titleBlock.parameters,
                    TOTAL_SHEETS_VISIBILITY_ALIASES
                  );
                  if (visibility.name) {
                    writes.push({
                      elementId: titleBlock.id,
                      parameterName: visibility.name,
                      value: true,
                      target: `${sheet.number || sheet.name} · рамка`,
                    });
                  }
                }
              }
            } else {
              warnings.push(
                "Параметр «Листов» не найден на листах — общее число листов не записано."
              );
            }
          }

          // 7. Auto-numbering (collision-safe: temp pass first when needed)
          const numberWrites: WriteOp[] = [];
          let renumbered: Array<{ from: string; to: string }> = [];
          if (args.autoNumber) {
            const numberParam = resolveParameterName(referenceParams, SHEET_NUMBER_ALIASES);
            if (!numberParam.name) {
              warnings.push("Параметр «Номер листа» не найден — автонумерация пропущена.");
            } else {
              const plan = buildAutoNumberPlan(
                sheets.map((sheet) => ({ id: sheet.id, number: sheet.number })),
                {
                  startNumber: args.autoNumber.startNumber ?? 1,
                  prefix: args.autoNumber.prefix ?? "",
                  padWidth: args.autoNumber.padWidth ?? 0,
                }
              );
              renumbered = plan.assignments.map(({ from, to }) => ({ from, to }));
              for (const temp of plan.tempAssignments) {
                numberWrites.push({
                  elementId: temp.id,
                  parameterName: numberParam.name,
                  value: temp.to,
                  target: temp.from,
                });
              }
              for (const assignment of plan.assignments) {
                numberWrites.push({
                  elementId: assignment.id,
                  parameterName: numberParam.name,
                  value: assignment.to,
                  target: assignment.from,
                });
              }
            }
          }

          const allWrites = [...writes, ...numberWrites];

          if (args.dryRun) {
            return {
              dryRun: true,
              sheetsInScope: sheets.map((sheet) => ({
                id: sheet.id,
                number: sheet.number,
                name: sheet.name,
              })),
              plannedWrites: allWrites.map((op) => ({
                target: op.target,
                parameterName: op.parameterName,
                value: op.value,
              })),
              renumbered,
              unresolvedFields,
            };
          }

          const { succeeded, failures } = await executeWrites(revitClient, allWrites);

          return {
            dryRun: false,
            sheetsProcessed: sheets.length,
            writesPlanned: allWrites.length,
            writesSucceeded: succeeded,
            renumbered,
            unresolvedFields,
            failures,
          };
        });

        const summaryLine = result.dryRun
          ? `Dry run: листов в объёме ${result.sheetsInScope!.length}, запланировано записей ${result.plannedWrites!.length}.`
          : `Заполнено: листов ${result.sheetsProcessed}, записей ${result.writesSucceeded}/${result.writesPlanned}` +
            (result.renumbered.length > 0
              ? `, перенумеровано ${result.renumbered.length}`
              : "") +
            ".";

        return {
          content: [
            { type: "text" as const, text: summaryLine },
            {
              type: "text" as const,
              text: JSON.stringify({ ...result, warnings }),
            },
          ],
          isError:
            !result.dryRun &&
            (result.failures?.length ?? 0) > 0 &&
            result.writesSucceeded === 0,
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `fill_title_block failed: ${
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
