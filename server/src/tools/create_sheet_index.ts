import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { fetchAllSheetsWithParameters } from "../utils/revitElementQuery.js";
import { findDuplicateNumbers } from "../utils/sheetReadiness.js";
import { findNumberingGaps } from "../utils/sheetIndex.js";
import { buildAutoNumberPlan, type SheetForNumbering } from "../utils/titleBlock.js";

/**
 * Индекс листов и сквозная нумерация комплекта (REV-174) — последний тикет
 * эпика «Что изменилось».
 *
 * Nothing here is new machinery: renumbering is `titleBlock.ts`'s
 * `buildAutoNumberPlan` (already built and tested for `fill_title_block`'s
 * own numbering, REV-47), duplicates are `sheetReadiness.ts`'s
 * `findDuplicateNumbers`, and the ведомость itself is `create_schedule`
 * pointed at OST_Sheets — the same generic Revit command every other
 * schedule tool already calls. What's genuinely new is `utils/sheetIndex.ts`
 * (gaps) and the plumbing that ties the three together.
 */

interface RevisionsResponse {
  success?: boolean;
  message?: string;
  sheets?: Array<{ sheetId: number; revisions: Array<{ sequenceNumber: number; description: string }> }>;
}

interface ScheduleResponse {
  success?: boolean;
  message?: string;
  viewId?: number;
  viewName?: string;
  warnings?: string[];
}

interface PlacementResponse {
  success?: boolean;
  message?: string;
}

interface SetParametersResponse {
  success?: boolean;
  message?: string;
  results?: Array<{ elementId: number; success: boolean; error?: string }>;
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

export function registerCreateSheetIndexTool(server: McpServer) {
  server.tool(
    "create_sheet_index",
    "Индекс листов комплекта — три действия в одном инструменте. " +
      "action: \"report\" (по умолчанию) — список листов с номером/именем/текущей ревизией, дубли " +
      "номеров и дыры в нумерации (см. check_sheet_readiness для готовности штампа — это разные " +
      "вопросы). " +
      "action: \"renumber\" — сквозная перенумерация: план «старый → новый» строится всегда; в модель " +
      "пишется, только если явно передан apply: true — без него это чистое превью, ничего не меняющее. " +
      "action: \"schedule\" — ведомость листов (create_schedule по категории Sheets) с номером, именем " +
      "и текущей ревизией; sheetIdToPlaceOn кладёт её сразу на лист. " +
      "Перенумеровать стоит до export_sheet_set, а не после — файлы, названные по старым номерам, " +
      "перенумерация не переименует.",
    {
      action: z.enum(["report", "renumber", "schedule"]).optional().default("report"),

      // renumber
      startNumber: z.number().int().optional().describe("renumber: с какого числа начинать. По умолчанию — с наименьшего текущего."),
      prefix: z.string().optional().describe("renumber: префикс перед числом, например «АР-»."),
      padWidth: z.number().int().min(0).max(6).optional().describe("renumber: ширина числа с ведущими нулями, 0 — без паддинга."),
      order: z.enum(["current", "given"]).optional().default("current").describe("renumber: \"current\" сохраняет естественный порядок текущих номеров, \"given\" — порядок sheetIds."),
      sheetIds: z.array(z.number().int()).optional().describe("renumber: какие листы перенумеровать. Без этого — все листы проекта."),
      apply: z.boolean().optional().default(false).describe("renumber: записать план в модель. По умолчанию false — только превью «старый → новый»."),

      // schedule
      scheduleName: z.string().optional().default("Ведомость листов").describe("schedule: имя вида-ведомости."),
      sheetIdToPlaceOn: z.number().int().optional().describe("schedule: положить ведомость на этот лист (x=20, y=70 мм от угла рамки)."),
    },
    async (args) => {
      try {
        if (args.action === "report") return await handleReport();
        if (args.action === "renumber") return await handleRenumber(args);
        return await handleSchedule(args);
      } catch (error) {
        return {
          content: [
            { type: "text" as const, text: `create_sheet_index не выполнен: ${error instanceof Error ? error.message : String(error)}` },
          ],
          isError: true,
        };
      }
    }
  );
}

async function handleReport() {
  const result = await withRevitConnection(async (revitClient) => {
    const { sheets, unreadable } = await fetchAllSheetsWithParameters(revitClient);
    if (sheets.length === 0 && unreadable.length === 0) return { empty: true as const };

    const revisionsResponse = (await revitClient.sendCommand("export_sheet_set", {
      action: "listRevisions",
    })) as RevisionsResponse;

    const latestRevisionBySheet = new Map<number, string>();
    if (revisionsResponse?.success !== false) {
      for (const entry of revisionsResponse?.sheets ?? []) {
        const latest = entry.revisions[entry.revisions.length - 1];
        if (latest) latestRevisionBySheet.set(entry.sheetId, latest.description);
      }
    }

    const duplicateNumbers = findDuplicateNumbers(sheets);
    const numberingGaps = findNumberingGaps(sheets.map((sheet) => sheet.number));

    return {
      empty: false as const,
      totalSheets: sheets.length,
      unreadableCount: unreadable.length,
      duplicateNumbers,
      numberingGaps,
      sheets: sheets
        .map((sheet) => ({
          id: sheet.id,
          number: sheet.number,
          name: sheet.name,
          revision: latestRevisionBySheet.get(sheet.id) ?? "",
        }))
        .sort((a, b) => a.number.localeCompare(b.number, undefined, { numeric: true })),
    };
  });

  if (result.empty) return failed("В проекте нет листов (OST_Sheets) — индексировать нечего.");

  const parts: string[] = [`Листов: ${result.totalSheets}`];
  if (result.duplicateNumbers.length > 0) parts.push(`дублей номеров: ${result.duplicateNumbers.length}`);
  if (result.numberingGaps.length > 0) {
    const missingCount = result.numberingGaps.reduce((sum, gap) => sum + gap.missing.length, 0);
    parts.push(`пропусков в нумерации: ${missingCount}`);
  }

  return ok({ success: true, ...result, message: parts.join(", ") + "." });
}

async function handleRenumber(args: {
  startNumber?: number;
  prefix?: string;
  padWidth?: number;
  order: "current" | "given";
  sheetIds?: number[];
  apply: boolean;
}) {
  const sheetsForPlan = await withRevitConnection(async (revitClient) => {
    const { sheets } = await fetchAllSheetsWithParameters(revitClient);
    const wanted = args.sheetIds ? new Set(args.sheetIds) : null;
    return sheets
      .filter((sheet) => !wanted || wanted.has(sheet.id))
      .map((sheet): SheetForNumbering => ({ id: sheet.id, number: sheet.number }));
  });

  if (sheetsForPlan.length === 0) {
    return failed(
      args.sheetIds ? "Ни один из переданных sheetIds не найден среди листов проекта." : "В проекте нет листов."
    );
  }

  const plan = buildAutoNumberPlan(sheetsForPlan, {
    startNumber: args.startNumber,
    prefix: args.prefix,
    padWidth: args.padWidth,
    order: args.order,
  });

  if (plan.assignments.length === 0) {
    return ok({
      success: true,
      applied: false,
      plan,
      message: "По этому правилу номера не меняются — план пуст.",
    });
  }

  if (!args.apply) {
    return ok({
      success: true,
      applied: false,
      plan,
      message: `${plan.assignments.length} лист(ов) сменят номер — превью, в модель не записано. Передайте apply: true, чтобы записать.`,
    });
  }

  // Same two-pass shape the plan itself computed: temp numbers first (Revit refuses two
  // sheets with the same number even mid-batch), then the final numbers.
  const writeNumbers = async (assignments: { id: number; to: string }[]) => {
    if (assignments.length === 0) return { success: true, results: [] } as SetParametersResponse;
    return (await withRevitConnection((client) =>
      client.sendCommand("set_elements_parameters", {
        edits: assignments.map((assignment) => ({
          elementId: assignment.id,
          parameters: { "Номер листа": assignment.to },
        })),
      })
    )) as SetParametersResponse;
  };

  const tempResult = await writeNumbers(plan.tempAssignments);
  if (tempResult?.success === false) {
    return failed(tempResult.message || "Не удалось записать временные номера листов.");
  }

  const finalResult = await writeNumbers(plan.assignments);

  const failedWrites = (finalResult?.results ?? []).filter((r) => !r.success);

  return ok({
    success: failedWrites.length === 0,
    applied: true,
    plan,
    failedWrites,
    message:
      failedWrites.length === 0
        ? `Перенумеровано ${plan.assignments.length} лист(ов).`
        : `Перенумеровано ${plan.assignments.length - failedWrites.length} из ${plan.assignments.length}, ${failedWrites.length} с ошибкой.`,
  });
}

async function handleSchedule(args: { scheduleName: string; sheetIdToPlaceOn?: number }) {
  const scheduleResponse = (await withRevitConnection((client) =>
    client.sendCommand("create_schedule", {
      schedule: {
        name: args.scheduleName,
        categoryName: "OST_Sheets",
        fields: [
          { parameterName: "Sheet Number|Номер листа", fieldType: "Instance", heading: "№" },
          { parameterName: "Sheet Name|Имя листа|Название листа", fieldType: "Instance", heading: "Наименование" },
          { parameterName: "Current Revision|Текущая ревизия", fieldType: "Instance", heading: "Ревизия" },
          {
            parameterName: "Current Revision Description|Описание текущей ревизии",
            fieldType: "Instance",
            heading: "Описание ревизии",
          },
        ],
        sortFields: [{ fieldName: "Sheet Number|Номер листа", sortOrder: "Ascending" }],
      },
    })
  )) as ScheduleResponse;

  if (scheduleResponse?.success === false) {
    return failed(scheduleResponse.message || "Не удалось создать ведомость листов.");
  }

  const warnings = [...(scheduleResponse?.warnings ?? [])];
  let placement: PlacementResponse | undefined;

  if (args.sheetIdToPlaceOn && scheduleResponse?.viewId) {
    placement = (await withRevitConnection((client) =>
      client.sendCommand("place_view_on_sheet", {
        placement: {
          sheetId: args.sheetIdToPlaceOn,
          viewId: scheduleResponse.viewId,
          positionX: 20,
          positionY: 70,
        },
      })
    )) as PlacementResponse;

    if (placement?.success === false) {
      warnings.push(`Ведомость создана, но не встала на лист: ${placement.message ?? "плагин отказал"}.`);
    }
  }

  return ok({
    success: true,
    viewId: scheduleResponse?.viewId,
    viewName: scheduleResponse?.viewName,
    placedOnSheet: args.sheetIdToPlaceOn && placement?.success !== false ? args.sheetIdToPlaceOn : null,
    warnings,
    message: args.sheetIdToPlaceOn
      ? placement?.success === false
        ? "Ведомость создана, на лист не встала — см. warnings."
        : "Ведомость создана и размещена на листе."
      : "Ведомость создана.",
  });
}
