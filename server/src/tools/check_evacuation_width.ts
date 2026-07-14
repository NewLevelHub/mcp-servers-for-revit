import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  DEFAULT_EVACUATION_WIDTH_PDF_FILES,
  loadEvacuationWidthRulesFromNormatives,
  pickPrimaryEvacuationWidthRule,
  type EvacuationWidthNormRule,
} from "../normatives/evacuationWidthRules.js";

const evacuationWidthItemSchema = z.object({
  id: z.number(),
  uniqueId: z.string(),
  name: z.string(),
  number: z.string(),
  level: z.string(),
  roomPurpose: z.string().optional(),
  actualWidthMm: z.number(),
  depthMm: z.number(),
  areaM2: z.number(),
  requiredWidthMm: z.number(),
  isCompliant: z.boolean(),
  deviationMm: z.number(),
});

const rawCheckResultSchema = z.object({
  success: z.boolean(),
  message: z.string(),
  mode: z.string().optional(),
  minWidthMm: z.number().nullable().optional(),
  corridorOnly: z.boolean().optional(),
  totalCorridorsChecked: z.number().optional(),
  violationCount: z.number().optional(),
  violations: z.array(evacuationWidthItemSchema).optional(),
  compliantCorridors: z.array(evacuationWidthItemSchema).optional(),
  highlightedCount: z.number().optional(),
});

export interface CheckEvacuationWidthResult {
  success: boolean;
  message: string;
  mode?: string;
  minWidthMm: number;
  corridorOnly?: boolean;
  totalCorridorsChecked: number;
  violationCount: number;
  nearLimitCount: number;
  violations: Array<z.infer<typeof evacuationWidthItemSchema>>;
  nearLimit: Array<z.infer<typeof evacuationWidthItemSchema>>;
  compliantCorridors: Array<z.infer<typeof evacuationWidthItemSchema>>;
  highlightedCount?: number;
  normativesDir?: string;
  normativePdfFiles?: string[];
  appliedRule?: EvacuationWidthNormRule | null;
  availableRules?: EvacuationWidthNormRule[];
  warnings?: string[];
}

function classifyNearLimit(
  items: Array<z.infer<typeof evacuationWidthItemSchema>>,
  minWidthMm: number,
  toleranceMm: number
) {
  const nearLimit: Array<z.infer<typeof evacuationWidthItemSchema>> = [];
  const violations: Array<z.infer<typeof evacuationWidthItemSchema>> = [];

  for (const item of items) {
    if (item.isCompliant) continue;
    if (item.deviationMm > 0 && item.deviationMm <= toleranceMm) {
      nearLimit.push(item);
    } else {
      violations.push(item);
    }
  }

  return { nearLimit, violations };
}

export function formatEvacuationWidthReport(result: CheckEvacuationWidthResult): string {
  const lines: string[] = [
    "## Проверка ширины эвакуационных коридоров",
    "",
    result.message,
    "",
    `- Требуемая ширина: **${result.minWidthMm} мм**`,
    `- Проверено помещений: **${result.totalCorridorsChecked}**`,
    `- Нарушений: **${result.violationCount}**`,
    `- Пограничных (в пределах ${result.nearLimitCount > 0 ? "допуска" : "—"}): **${result.nearLimitCount}**`,
  ];

  if (result.appliedRule) {
    lines.push(
      `- Норматив: **${result.appliedRule.source.document}**${result.appliedRule.source.clause ? `, ${result.appliedRule.source.clause}` : ""}`
    );
    lines.push(`  - «${result.appliedRule.source.quote}»`);
  }

  if (result.normativesDir) {
    lines.push(`- Каталог normatives: \`${result.normativesDir}\``);
  }

  lines.push("");

  if (result.warnings && result.warnings.length > 0) {
    lines.push("### Предупреждения");
    for (const warning of result.warnings) {
      lines.push(`- ${warning}`);
    }
    lines.push("");
  }

  if (result.violations.length > 0) {
    lines.push("### Нарушения");
    for (const item of result.violations) {
      lines.push(
        `- **${item.name || item.number}** (id ${item.id}, ${item.level}) — факт **${Math.round(item.actualWidthMm)} мм**, не хватает **${Math.round(item.deviationMm)} мм**`
      );
      if (item.roomPurpose) {
        lines.push(`  - Назначение: ${item.roomPurpose}`);
      }
    }
    lines.push("");
  }

  if (result.nearLimit.length > 0) {
    lines.push("### Пограничные (близко к норме)");
    for (const item of result.nearLimit) {
      lines.push(
        `- **${item.name || item.number}** (id ${item.id}) — **${Math.round(item.actualWidthMm)} мм** (норма ${result.minWidthMm} мм)`
      );
    }
    lines.push("");
  }

  if (
    result.violations.length === 0 &&
    result.nearLimit.length === 0 &&
    result.totalCorridorsChecked > 0
  ) {
    lines.push("Все проверенные коридоры соответствуют требуемой ширине.");
  }

  if (result.availableRules && result.availableRules.length > 1) {
    lines.push("### Другие найденные нормы по ширине");
    for (const rule of result.availableRules.slice(0, 5)) {
      lines.push(
        `- ${rule.object}: **${rule.minWidthMm} мм** — ${rule.source.document}${rule.source.clause ? `, ${rule.source.clause}` : ""}`
      );
    }
  }

  return lines.join("\n");
}

export function registerCheckEvacuationWidthTool(server: McpServer) {
  server.tool(
    "check_evacuation_width",
    "Check evacuation corridor widths against norms from repo/normatives PDFs (ГОСТ/СП/СН РК) or an explicit minWidthMm. Reads rules automatically when minWidthMm is omitted. Compares with actual room widths from Revit (bounding footprint, mm). Reports violations with element ids. Mode 'highlight' paints Room Tag labels via Override Graphics in View (Projection Lines color) — same as selecting a марка помещения and setting color; does not solid-fill the room. Use highlightTarget='violations' (default, red), 'compliant' (green), or 'both'. Checks rooms named or designated as corridors, tambours, lift halls, stairs, etc.",
    {
      minWidthMm: z
        .number()
        .positive()
        .optional()
        .describe(
          "Minimum required width in mm. Omit to auto-load from normatives/ PDFs."
        ),
      source: z
        .object({
          document: z.string(),
          clause: z.string(),
          quote: z.string(),
          page: z.number().int().positive().optional(),
        })
        .optional()
        .describe("Override normative citation when passing minWidthMm manually."),
      buildingClass: z
        .string()
        .optional()
        .describe("Optional building class filter for rule selection, e.g. Ф1.1."),
      normativePdfFiles: z
        .array(z.string())
        .optional()
        .describe("PDF file names from repo/normatives for auto rule loading."),
      scanAllNormativePdfs: z
        .boolean()
        .optional()
        .default(false)
        .describe("Scan all PDFs in normatives/ instead of the default pilot list."),
      nearLimitToleranceMm: z
        .number()
        .nonnegative()
        .optional()
        .default(100)
        .describe(
          "Flag violators within this many mm of the limit as 'near limit' (e.g. 1130 vs 1200)."
        ),
      mode: z.enum(["report", "highlight"]).optional().default("report"),
      levelName: z.string().optional().default(""),
      roomNameFilter: z.string().optional().default(""),
      corridorOnly: z.boolean().optional().default(true),
      includeCompliant: z.boolean().optional().default(false),
      highlightTarget: z
        .enum(["violations", "compliant", "both"])
        .optional()
        .default("violations")
        .describe(
          "Which rooms to paint in mode=highlight: violations (default), compliant, or both. Colors Room Tags via Override Graphics → Projection Lines."
        ),
      highlightColor: z
        .object({
          r: z.number().int().min(0).max(255),
          g: z.number().int().min(0).max(255),
          b: z.number().int().min(0).max(255),
        })
        .optional()
        .describe("Color for violations / highlightTarget=violations. Default red."),
      compliantHighlightColor: z
        .object({
          r: z.number().int().min(0).max(255),
          g: z.number().int().min(0).max(255),
          b: z.number().int().min(0).max(255),
        })
        .optional()
        .describe(
          "Color for compliant rooms when highlightTarget is compliant|both. Default green {0,180,0}."
        ),
    },
    async (args) => {
      try {
        const { rules, warnings, normativesDir } =
          await loadEvacuationWidthRulesFromNormatives({
            pdfFiles: args.normativePdfFiles,
            scanAllPdfs: args.scanAllNormativePdfs ?? false,
          });

        const appliedRule =
          args.minWidthMm === undefined
            ? pickPrimaryEvacuationWidthRule(rules, {
                buildingClass: args.buildingClass,
              })
            : null;

        const minWidthMm = args.minWidthMm ?? appliedRule?.minWidthMm;
        if (minWidthMm === undefined) {
          return {
            content: [
              {
                type: "text",
                text:
                  "check_evacuation_width failed: could not determine minWidthMm. " +
                  "Pass minWidthMm explicitly or ensure normatives/ PDFs are available.",
              },
            ],
            isError: true,
          };
        }

        const source =
          args.source ??
          (appliedRule
            ? appliedRule.source
            : {
                document: "—",
                clause: "",
                quote: "Значение передано вручную без цитаты норматива.",
              });

        const params = {
          minWidthMm,
          mode: args.mode ?? "report",
          levelName: args.levelName ?? "",
          roomNameFilter: args.roomNameFilter ?? "",
          corridorOnly: args.corridorOnly ?? true,
          includeCompliant: args.includeCompliant ?? false,
          highlightTarget: args.highlightTarget ?? "violations",
          highlightColor: args.highlightColor,
          compliantHighlightColor: args.compliantHighlightColor,
        };

        const rawResponse = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("check_evacuation_width", params);
        });

        const raw = rawCheckResultSchema.parse(rawResponse);
        if (!raw.success) {
          return {
            content: [
              {
                type: "text",
                text: raw.message || "check_evacuation_width failed.",
              },
            ],
            isError: true,
          };
        }

        const toleranceMm = args.nearLimitToleranceMm ?? 100;
        const rawViolations = raw.violations ?? [];
        const { nearLimit, violations } = classifyNearLimit(
          rawViolations,
          minWidthMm,
          toleranceMm
        );

        const pdfFiles =
          args.normativePdfFiles ??
          (args.scanAllNormativePdfs ? ["*"] : [...DEFAULT_EVACUATION_WIDTH_PDF_FILES]);

        const result: CheckEvacuationWidthResult = {
          success: true,
          message: raw.message,
          mode: raw.mode,
          minWidthMm,
          corridorOnly: raw.corridorOnly,
          totalCorridorsChecked: raw.totalCorridorsChecked ?? 0,
          violationCount: violations.length,
          nearLimitCount: nearLimit.length,
          violations,
          nearLimit,
          compliantCorridors: raw.compliantCorridors ?? [],
          highlightedCount: raw.highlightedCount,
          normativesDir,
          normativePdfFiles: pdfFiles,
          appliedRule: appliedRule ?? (args.minWidthMm ? null : null),
          availableRules: rules,
          warnings,
        };

        if (appliedRule) {
          result.appliedRule = appliedRule;
        }

        const report = formatEvacuationWidthReport(result);

        return {
          content: [
            { type: "text", text: report },
            {
              type: "text",
              text: JSON.stringify(
                {
                  norm: {
                    minWidthMm,
                    source,
                    appliedRule: result.appliedRule,
                  },
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
              text: `check_evacuation_width failed: ${
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
