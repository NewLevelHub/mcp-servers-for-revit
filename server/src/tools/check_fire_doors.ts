import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { applyFireDoorRules } from "../normatives/applyFireDoorRules.js";
import {
  DEFAULT_FIRE_DOOR_PDF_FILES,
  loadFireDoorRulesFromNormatives,
} from "../normatives/fireDoorRules.js";

const doorFireFactsSchema = z.object({
  id: z.number(),
  uniqueId: z.string(),
  mark: z.string(),
  family: z.string(),
  type: z.string(),
  level: z.string(),
  fromRoom: z.string(),
  toRoom: z.string(),
  openingWidthMm: z.number().nullable().optional(),
  isOnEgressPath: z.boolean(),
  isMarkedAsFireDoor: z.boolean(),
  currentFireRating: z.string(),
});

const rawCheckResultSchema = z.object({
  success: z.boolean(),
  message: z.string(),
  totalDoors: z.number(),
  doors: z.array(doorFireFactsSchema),
});

const normativeSourceSchema = z.object({
  document: z.string(),
  clause: z.string(),
  quote: z.string(),
});

const fireDoorCheckItemSchema = doorFireFactsSchema.extend({
  requiresFireDoor: z.boolean(),
  ruleId: z.string(),
  reason: z.string(),
  source: normativeSourceSchema,
  compliant: z.boolean(),
});

export type FireDoorCheckItem = z.infer<typeof fireDoorCheckItemSchema>;

export interface CheckFireDoorsResult {
  success: boolean;
  message: string;
  mode?: string;
  totalDoors: number;
  requiredFireDoors: number;
  nonCompliantCount: number;
  doors: FireDoorCheckItem[];
  normativesDir?: string;
  normativePdfFiles?: string[];
  appliedRuleCount?: number;
  warnings?: string[];
}

export function formatFireDoorReport(result: CheckFireDoorsResult): string {
  const required = result.doors.filter((door) => door.requiresFireDoor);
  const nonCompliant = required.filter((door) => !door.compliant);

  const lines: string[] = [
    `## Проверка противопожарных дверей`,
    ``,
    result.message,
    ``,
    `- Всего дверей: **${result.totalDoors}**`,
    `- Должны быть противопожарными: **${result.requiredFireDoors}**`,
    `- Несоответствий: **${result.nonCompliantCount}**`,
  ];

  if (result.normativesDir) {
    lines.push(`- Источник норм: \`${result.normativesDir}\``);
  }
  if (result.appliedRuleCount !== undefined) {
    lines.push(`- Извлечено правил из PDF: **${result.appliedRuleCount}**`);
  }
  lines.push(``);

  if (result.warnings && result.warnings.length > 0) {
    lines.push(`### Предупреждения`);
    for (const warning of result.warnings) {
      lines.push(`- ${warning}`);
    }
    lines.push(``);
  }

  if (required.length === 0) {
    lines.push(
      `По правилам из normatives/ противопожарные двери для текущей модели не требуются.`
    );
    return lines.join("\n");
  }

  lines.push(`### Двери, которые должны быть противопожарными`);
  lines.push(``);

  for (const door of required) {
    const status = door.compliant ? "соответствует" : "НЕ соответствует";
    lines.push(
      `- **${door.mark || door.id}** (${door.family} / ${door.type}, ${door.level}) — ${status}`
    );
    lines.push(`  - Помещения: «${door.fromRoom || "—"}» → «${door.toRoom || "—"}»`);
    lines.push(`  - Причина: ${door.reason}`);
    lines.push(
      `  - Норматив: ${door.source.document}${door.source.clause ? `, ${door.source.clause}` : ""} — «${door.source.quote}»`
    );
    if (door.currentFireRating) {
      lines.push(`  - Текущая маркировка: ${door.currentFireRating}`);
    }
    lines.push(``);
  }

  if (nonCompliant.length > 0) {
    lines.push(`### Требуют проставления признака (${nonCompliant.length})`);
    for (const door of nonCompliant) {
      lines.push(`- id ${door.id}: ${door.mark || door.type} (${door.level})`);
    }
  }

  return lines.join("\n");
}

async function writeFireDoorMarks(
  doors: FireDoorCheckItem[],
  parameterName: string,
  markValue: string | boolean | number
): Promise<Array<{ elementId: number; success: boolean; message: string }>> {
  const targets = doors.filter(
    (door) => door.requiresFireDoor && !door.compliant
  );
  const results: Array<{ elementId: number; success: boolean; message: string }> =
    [];

  for (const door of targets) {
    try {
      const response = await withRevitConnection(async (revitClient) => {
        return await revitClient.sendCommand("set_element_parameter", {
          elementId: door.id,
          parameterName,
          value: markValue,
        });
      });

      const success =
        typeof response === "object" &&
        response !== null &&
        "success" in response &&
        Boolean((response as { success?: boolean }).success);

      results.push({
        elementId: door.id,
        success,
        message:
          typeof response === "object" && response !== null && "message" in response
            ? String((response as { message?: string }).message)
            : success
              ? "Parameter updated."
              : "Unknown response.",
      });
    } catch (error) {
      results.push({
        elementId: door.id,
        success: false,
        message: error instanceof Error ? error.message : String(error),
      });
    }
  }

  return results;
}

export function registerCheckFireDoorsTool(server: McpServer) {
  server.tool(
    "check_fire_doors",
    "Pilot normative check: reads fire-door rules from repo/normatives PDFs (ГОСТ/СП/СН РК), compares with Revit doors, returns report or writes marks via set_element_parameter.",
    {
      mode: z
        .enum(["report", "write"])
        .default("report")
        .describe(
          "report — only return results in chat; write — also set parameter/mark on non-compliant doors."
        ),
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Optional level name filter."),
      normativePdfFiles: z
        .array(z.string())
        .optional()
        .describe(
          "Optional PDF file names from repo/normatives. Default: pilot set for residential blocks."
        ),
      scanAllNormativePdfs: z
        .boolean()
        .optional()
        .default(false)
        .describe("Scan all PDFs in normatives/ instead of the default pilot list."),
      parameterName: z
        .string()
        .optional()
        .default("Противопожарная")
        .describe("Parameter name for write mode (REV-28)."),
      markValue: z
        .union([z.string(), z.boolean(), z.number()])
        .optional()
        .default("Да")
        .describe("Value to write in write mode."),
    },
    async (args) => {
      const params = {
        levelName: args.levelName ?? "",
      };

      try {
        const { rules, warnings, normativesDir } =
          await loadFireDoorRulesFromNormatives({
            pdfFiles: args.normativePdfFiles,
            scanAllPdfs: args.scanAllNormativePdfs ?? false,
          });

        const rawResponse = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("check_fire_doors", params);
        });

        const raw = rawCheckResultSchema.parse(rawResponse);
        if (!raw.success) {
          return {
            content: [
              {
                type: "text",
                text: raw.message || "check_fire_doors failed.",
              },
            ],
            isError: true,
          };
        }

        const applied = applyFireDoorRules(raw.doors, rules);
        const pdfFiles =
          args.normativePdfFiles ??
          (args.scanAllNormativePdfs ? ["*"] : [...DEFAULT_FIRE_DOOR_PDF_FILES]);

        const result: CheckFireDoorsResult = {
          success: true,
          message: applied.message,
          mode: args.mode,
          totalDoors: applied.totalDoors,
          requiredFireDoors: applied.requiredFireDoors,
          nonCompliantCount: applied.nonCompliantCount,
          doors: applied.doors,
          normativesDir,
          normativePdfFiles: pdfFiles,
          appliedRuleCount: rules.length,
          warnings,
        };

        let writeResults:
          | Array<{ elementId: number; success: boolean; message: string }>
          | undefined;

        if (args.mode === "write") {
          writeResults = await writeFireDoorMarks(
            result.doors,
            args.parameterName ?? "Противопожарная",
            args.markValue ?? "Да"
          );
        }

        const report = formatFireDoorReport(result);
        const writeSummary =
          args.mode === "write" && writeResults
            ? [
                ``,
                `### Запись в модель (set_element_parameter)`,
                `- Обработано: ${writeResults.length}`,
                `- Успешно: ${writeResults.filter((item) => item.success).length}`,
                `- Ошибок: ${writeResults.filter((item) => !item.success).length}`,
              ].join("\n")
            : "";

        return {
          content: [
            {
              type: "text",
              text: report + writeSummary,
            },
            {
              type: "text",
              text: JSON.stringify(
                {
                  ...result,
                  appliedRules: applied.appliedRules,
                  writeResults,
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
              text: `check_fire_doors failed: ${
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
