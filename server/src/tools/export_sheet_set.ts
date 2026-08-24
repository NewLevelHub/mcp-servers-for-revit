import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { fetchAllSheetsWithParameters, fetchParametersBatch, findParamValue, parseFilteredElements } from "../utils/revitElementQuery.js";
import { PROJECT_FIELD_ALIASES } from "../utils/titleBlock.js";
import {
  DEFAULT_FILENAME_TEMPLATE,
  selectSheetsForExport,
  type SelectedSheet,
  type SheetRevisionInfo,
  type SkippedSheet,
} from "../utils/exportSheetSet.js";

type SelectionOutcome =
  | { kind: "error"; message: string }
  | { kind: "empty" }
  | { kind: "ok"; selected: SelectedSheet[]; skipped: SkippedSheet[] };

/**
 * Выпуск комплекта — PDF/DWG пачкой, named by the organisation's own template
 * (REV-173). Closes the cycle REV-47's check_sheet_readiness left open: a
 * check with nothing that reads its answer and acts on it.
 *
 * Which sheets go out and what each is named is decided here, in TypeScript,
 * on top of `check_sheet_readiness`'s own grading — tested in
 * `exportSheetSet.test.ts` without Revit. The plugin is handed a finished
 * {sheetId, fileName} list and two questions it alone can answer: which
 * revisions sit on which sheet, and can this particular sheet actually export.
 */

interface RevisionsResponse {
  success?: boolean;
  message?: string;
  sheets?: Array<{ sheetId: number; revisions: Array<{ sequenceNumber: number; description: string }> }>;
}

interface ExportResponse {
  success?: boolean;
  message?: string;
  outputDir?: string;
  dwgSetupUsed?: string;
  results?: Array<{
    sheetId: number;
    fileName: string;
    pdfPath?: string;
    dwgPath?: string;
    success: boolean;
    error?: string;
  }>;
  availableDwgSetups?: string[];
}

function ok(payload: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(payload) }] };
}

function failed(message: string) {
  return {
    content: [{ type: "text" as const, text: JSON.stringify({ success: false, message }) }],
    isError: true,
  };
}

export function registerExportSheetSetTool(server: McpServer) {
  server.tool(
    "export_sheet_set",
    "Выпуск комплекта: печатает PDF и/или экспортирует DWG пачкой листов сразу, с именами файлов по " +
      "шаблону организации — по умолчанию «{code}-{discipline}-{number}_{name}_{revision}» " +
      "(«2024-15-АР-АР-01_Фасады_3»), меняется через fileNameTemplate. " +
      "Отбор листов — тремя способами сразу, по необходимости: sheetIds (по списку), discipline (по разделу — " +
      "«АР»/«КР», из штампа или по номеру листа), revisionDescription (по ревизии — только листы, на которых " +
      "она показана). " +
      "Лист, не прошедший check_sheet_readiness (те же четыре подписи), по умолчанию НЕ выпускается — только " +
      "с allowNotReady: true. Один битый или пустой лист не роняет всю пачку — попадает в failed с причиной, " +
      "остальные экспортируются. " +
      "Настройки DWG (слои, версия файла) берутся из именованного экспорта проекта (dwgSetupName) — свои не " +
      "изобретаются; если в проекте больше одной настройки и dwgSetupName не передан, отказывает и называет " +
      "варианты. " +
      "Перед выпуском стоит спросить check_sheet_readiness напрямую, если нужно решить, что дозаполнить, а не " +
      "просто пропустить.",
    {
      format: z.enum(["pdf", "dwg", "both"]).optional().default("pdf").describe("Что выпускать. По умолчанию pdf."),
      outputDir: z.string().min(1).describe("Папка на машине с Revit, куда пишутся файлы. Должна существовать."),
      sheetIds: z.array(z.number().int()).optional().describe("Явный список листов — «по списку». Без него берутся все листы проекта, дальше режется остальными фильтрами."),
      discipline: z.string().optional().describe("«По разделу» — «АР», «КР» и т.п., сравнение без учёта регистра. Раздел листа читается из штампа, а если строки нет — из букв перед первым разделителем в номере листа («АР-01» → «АР»)."),
      revisionDescription: z.string().optional().describe("«По ревизии» — только листы, на которых показана ревизия с таким описанием (см. create_revision_clouds / compare_model_versions)."),
      fileNameTemplate: z
        .string()
        .optional()
        .default(DEFAULT_FILENAME_TEMPLATE)
        .describe(`Шаблон имени файла, плейсхолдеры {code} {discipline} {number} {name} {revision}. По умолчанию ${DEFAULT_FILENAME_TEMPLATE}.`),
      allowNotReady: z
        .boolean()
        .optional()
        .default(false)
        .describe("Выпускать и листы, не прошедшие check_sheet_readiness. По умолчанию такие листы пропускаются."),
      readinessFields: z
        .array(z.enum(["drawnBy", "checkedBy", "chiefEngineer", "normControl", "issueDate", "totalSheets"]))
        .optional()
        .describe("Какие поля штампа проверять на готовность — как в check_sheet_readiness. По умолчанию четыре подписи."),
      dwgSetupName: z.string().optional().describe("Именованная настройка экспорта DWG из проекта. Обязательна, если в проекте их больше одной."),
      dryRun: z.boolean().optional().default(false).describe("Посчитать, что выпустится и что пропустится, но не создавать файлов."),
    },
    async (args) => {
      try {
        const needsRevisionData =
          Boolean(args.revisionDescription) || args.fileNameTemplate.toLowerCase().includes("{revision}");

        const selection = await withRevitConnection(async (revitClient): Promise<SelectionOutcome> => {
          const { sheets, unreadable } = await fetchAllSheetsWithParameters(revitClient);
          if (sheets.length === 0 && unreadable.length === 0) {
            return { kind: "empty" };
          }

          let projectCode = "";
          const projectInfoResponse = await revitClient.sendCommand("ai_element_filter", {
            data: { filterCategory: "OST_ProjectInformation", includeTypes: false, includeInstances: true },
          });
          const projectInfo = parseFilteredElements(projectInfoResponse)[0];
          if (projectInfo) {
            const projectParams = await fetchParametersBatch(revitClient, [projectInfo.id]);
            projectCode = findParamValue(projectParams.get(projectInfo.id)?.parameters ?? [], PROJECT_FIELD_ALIASES.code);
          }

          const revisionsBySheet = new Map<number, SheetRevisionInfo>();
          if (needsRevisionData) {
            const revisionsResponse = (await revitClient.sendCommand("export_sheet_set", {
              action: "listRevisions",
            })) as RevisionsResponse;
            if (revisionsResponse?.success === false) {
              return { kind: "error", message: revisionsResponse.message || "Не удалось прочитать ревизии листов." };
            }
            for (const entry of revisionsResponse?.sheets ?? []) {
              revisionsBySheet.set(entry.sheetId, { sheetId: entry.sheetId, revisions: entry.revisions });
            }
          }

          const result = selectSheetsForExport(sheets, unreadable, revisionsBySheet, {
            sheetIds: args.sheetIds,
            discipline: args.discipline,
            revisionDescription: args.revisionDescription,
            readinessFields: args.readinessFields,
            allowNotReady: args.allowNotReady,
            fileNameTemplate: args.fileNameTemplate,
            projectCode,
          });

          return { kind: "ok", ...result };
        });

        if (selection.kind === "error") return failed(selection.message);
        if (selection.kind === "empty") return failed("В проекте нет листов (OST_Sheets) — выпускать нечего.");

        const { selected, skipped } = selection;
        if (selected.length === 0) {
          return failed(
            `Ни один лист не прошёл отбор — ${skipped.length} пропущено. ` +
              `Причины: ${[...new Set(skipped.map((s) => s.reason))].join(", ")}.`
          );
        }

        if (args.dryRun) {
          return ok({
            success: true,
            dryRun: true,
            wouldExport: selected.length,
            skipped: skipped.length,
            files: selected.map((s) => ({ sheetId: s.sheetId, number: s.number, fileName: s.fileName })),
            skippedDetail: skipped,
            message: `${selected.length} лист${selected.length === 1 ? "" : "ов"} выпустилось бы, ${skipped.length} пропущено (dryRun).`,
          });
        }

        const response = (await withRevitConnection((client) =>
          client.sendCommand("export_sheet_set", {
            action: "export",
            format: args.format,
            outputDir: args.outputDir,
            dwgSetupName: args.dwgSetupName,
            items: selected.map((s) => ({ sheetId: s.sheetId, fileName: s.fileName })),
          })
        )) as ExportResponse;

        if (response?.success === false) {
          const base = response.message || "Плагин не смог выпустить комплект.";
          const setups = response.availableDwgSetups;
          return failed(
            setups && setups.length > 0 ? `${base} Доступные настройки: ${setups.join(", ")}.` : base
          );
        }

        const results = response?.results ?? [];
        const succeeded = results.filter((r) => r.success);
        const failedItems = results.filter((r) => !r.success);

        return ok({
          success: failedItems.length === 0,
          outputDir: response?.outputDir,
          dwgSetupUsed: response?.dwgSetupUsed,
          exported: succeeded,
          exportedCount: succeeded.length,
          failed: failedItems,
          failedCount: failedItems.length,
          skipped,
          skippedCount: skipped.length,
          message:
            `Выпущено ${succeeded.length} из ${results.length} листов` +
            (failedItems.length ? `, ${failedItems.length} с ошибкой` : "") +
            (skipped.length ? `, ${skipped.length} пропущено до экспорта` : "") +
            ".",
        });
      } catch (error) {
        return {
          content: [
            { type: "text" as const, text: `export_sheet_set не выполнен: ${error instanceof Error ? error.message : String(error)}` },
          ],
          isError: true,
        };
      }
    }
  );
}
