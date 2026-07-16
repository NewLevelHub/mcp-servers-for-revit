import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  DEFAULT_MIN_DIMENSIONS_PDF_FILES,
  loadMinDimensionRulesFromNormatives,
  resolveMinDimensionLimits,
  type MinDimensionNormRule,
  type ResolvedMinDimensionLimits,
} from "../normatives/minDimensionsRules.js";

const minDimensionItemSchema = z.object({
  id: z.number(),
  uniqueId: z.string(),
  name: z.string(),
  number: z.string(),
  level: z.string(),
  spaceKind: z.string(),
  checkType: z.string(),
  metric: z.string(),
  actualValueMm: z.number(),
  requiredValueMm: z.number(),
  isCompliant: z.boolean(),
  deviationMm: z.number(),
  widthMm: z.number().nullable().optional(),
  depthMm: z.number().nullable().optional(),
  areaM2: z.number().nullable().optional(),
  wallId: z.number().nullable().optional(),
  pierKind: z.string().optional(),
  adjacentOpeningIds: z.array(z.number()).optional(),
});

const rawCheckResultSchema = z.object({
  success: z.boolean(),
  message: z.string(),
  mode: z.string().optional(),
  minBalconyWidthMm: z.number().nullable().optional(),
  minLoggiaWidthMm: z.number().nullable().optional(),
  minLoggiaDepthMm: z.number().nullable().optional(),
  minFirePierToOpeningMm: z.number().nullable().optional(),
  minFirePierBetweenOpeningsMm: z.number().nullable().optional(),
  totalSpacesChecked: z.number().optional(),
  violationCount: z.number().optional(),
  violations: z.array(minDimensionItemSchema).optional(),
  compliantItems: z.array(minDimensionItemSchema).optional(),
  highlightedCount: z.number().optional(),
});

export interface CheckMinDimensionsResult {
  success: boolean;
  message: string;
  mode?: string;
  limits: ResolvedMinDimensionLimits;
  totalSpacesChecked: number;
  violationCount: number;
  violations: Array<z.infer<typeof minDimensionItemSchema>>;
  compliantItems: Array<z.infer<typeof minDimensionItemSchema>>;
  highlightedCount?: number;
  normativesDir?: string;
  normativePdfFiles?: string[];
  availableRules?: MinDimensionNormRule[];
  warnings?: string[];
}

const METRIC_LABELS: Record<string, string> = {
  width: "ширина",
  depth: "глубина",
  pier_to_opening: "простенок до проёма",
  pier_between_openings: "простенок между проёмами",
};

export function formatMinDimensionsReport(result: CheckMinDimensionsResult): string {
  const lines: string[] = [
    "## Проверка минимальных размеров лоджий, балконов и противопожарных простенков",
    "",
    result.message,
    "",
    `- Проверено помещений: **${result.totalSpacesChecked}**`,
    `- Нарушений: **${result.violationCount}**`,
  ];

  const { limits } = result;
  if (limits.minBalconyWidthMm) {
    lines.push(`- Мин. ширина балкона: **${limits.minBalconyWidthMm} мм**`);
  }
  if (limits.minLoggiaWidthMm) {
    lines.push(`- Мин. ширина лоджии: **${limits.minLoggiaWidthMm} мм**`);
  }
  if (limits.minLoggiaDepthMm) {
    lines.push(`- Мин. глубина лоджии/балкона: **${limits.minLoggiaDepthMm} мм**`);
  }
  if (limits.minFirePierToOpeningMm) {
    lines.push(
      `- Мин. простенок до проёма: **${limits.minFirePierToOpeningMm} мм**`
    );
  }
  if (limits.minFirePierBetweenOpeningsMm) {
    lines.push(
      `- Мин. простенок между проёмами: **${limits.minFirePierBetweenOpeningsMm} мм**`
    );
  }

  if (limits.appliedRules.length > 0) {
    lines.push("", "### Применённые нормы");
    for (const rule of limits.appliedRules) {
      lines.push(
        `- **${rule.object}** (${METRIC_LABELS[rule.metric] ?? rule.metric}): ${rule.minValueMm} мм — ${rule.source.document}${rule.source.clause ? `, ${rule.source.clause}` : ""}`
      );
      lines.push(`  - «${rule.source.quote}»`);
    }
  }

  if (result.normativesDir) {
    lines.push("", `- Каталог normatives: \`${result.normativesDir}\``);
  }

  if (result.warnings && result.warnings.length > 0) {
    lines.push("", "### Предупреждения");
    for (const warning of result.warnings) {
      lines.push(`- ${warning}`);
    }
  }

  if (result.violations.length > 0) {
    lines.push("", "### Нарушения");
    for (const item of result.violations) {
      const metricLabel = METRIC_LABELS[item.metric] ?? item.metric;
      lines.push(
        `- **${item.name || item.number}** (${item.spaceKind}, id ${item.id}, ${item.level}) — ${metricLabel}: факт **${Math.round(item.actualValueMm)} мм**, норма **${Math.round(item.requiredValueMm)} мм**, не хватает **${Math.round(item.deviationMm)} мм**`
      );
      if (item.wallId) {
        lines.push(`  - Стена id ${item.wallId}`);
      }
      if (item.adjacentOpeningIds && item.adjacentOpeningIds.length > 0) {
        lines.push(`  - Проёмы: ${item.adjacentOpeningIds.join(", ")}`);
      }
    }
  } else if (result.totalSpacesChecked > 0) {
    lines.push("", "Все проверенные лоджии/балконы соответствуют применённым нормам.");
  } else {
    lines.push(
      "",
      "Лоджии/балконы не найдены. Убедитесь, что помещения названы «лоджия», «балкон» или «терраса»."
    );
  }

  return lines.join("\n");
}

export function registerCheckMinDimensionsTool(server: McpServer) {
  server.tool(
    "check_min_dimensions",
    "Check minimum dimensions of balconies, loggias, and fire-resistant piers (противопожарные простенки) against norms from repo/normatives PDFs (ГОСТ/СП/СН РК) or explicit limits. Identifies rooms by name/purpose, compares width/depth footprint (mm) and pier distances between glazed openings on facade walls. Returns violations with element ids; mode 'highlight' colors violating room tag labels (name/area) red in the active view without filling the room.",
    {
      minBalconyWidthMm: z
        .number()
        .positive()
        .optional()
        .describe("Minimum balcony width in mm. Omit to auto-load from normatives/."),
      minLoggiaWidthMm: z
        .number()
        .positive()
        .optional()
        .describe("Minimum loggia width in mm. Omit to auto-load from normatives/."),
      minLoggiaDepthMm: z
        .number()
        .positive()
        .optional()
        .describe("Minimum loggia/balcony depth in mm. Omit to auto-load from normatives/."),
      minFirePierToOpeningMm: z
        .number()
        .positive()
        .optional()
        .describe(
          "Minimum fire pier from balcony end to glazed opening in mm. Omit to auto-load."
        ),
      minFirePierBetweenOpeningsMm: z
        .number()
        .positive()
        .optional()
        .describe(
          "Minimum fire pier between glazed openings in mm. Omit to auto-load."
        ),
      source: z
        .object({
          document: z.string(),
          clause: z.string(),
          quote: z.string(),
          page: z.number().int().positive().optional(),
        })
        .optional()
        .describe("Override normative citation when passing limits manually."),
      normativePdfFiles: z
        .array(z.string())
        .optional()
        .describe("PDF file names from repo/normatives for auto rule loading."),
      scanAllNormativePdfs: z
        .boolean()
        .optional()
        .default(false)
        .describe("Scan all PDFs in normatives/ instead of the default pilot list."),
      checkFirePiers: z
        .boolean()
        .optional()
        .default(true)
        .describe("Also check fire pier distances on facade walls."),
      mode: z.enum(["report", "highlight"]).optional().default("report"),
      levelName: z.string().optional().default(""),
      roomNameFilter: z.string().optional().default(""),
      includeCompliant: z.boolean().optional().default(false),
      highlightColor: z
        .object({
          r: z.number().int().min(0).max(255),
          g: z.number().int().min(0).max(255),
          b: z.number().int().min(0).max(255),
        })
        .optional(),
    },
    async (args) => {
      try {
        const { rules, warnings, normativesDir } =
          await loadMinDimensionRulesFromNormatives({
            pdfFiles: args.normativePdfFiles,
            scanAllPdfs: args.scanAllNormativePdfs ?? false,
          });

        const limits = resolveMinDimensionLimits(rules, {
          housingType: "ordinary",
          minBalconyWidthMm: args.minBalconyWidthMm,
          minLoggiaWidthMm: args.minLoggiaWidthMm,
          minLoggiaDepthMm: args.minLoggiaDepthMm,
          minFirePierToOpeningMm: args.minFirePierToOpeningMm,
          minFirePierBetweenOpeningsMm: args.minFirePierBetweenOpeningsMm,
        });

        const hasLimit =
          limits.minBalconyWidthMm !== undefined ||
          limits.minLoggiaWidthMm !== undefined ||
          limits.minLoggiaDepthMm !== undefined ||
          limits.minFirePierToOpeningMm !== undefined ||
          limits.minFirePierBetweenOpeningsMm !== undefined;

        if (!hasLimit) {
          const skipNote =
            limits.skippedMgnRules > 0
              ? ` Пропущено ${limits.skippedMgnRules} правил МГН/спецжилья (для обычного жилья не применяются).`
              : "";
          return {
            content: [
              {
                type: "text",
                text:
                  "check_min_dimensions: для обычного жилья нет применимой мин. ширины/глубины лоджий-балконов." +
                  skipNote +
                  " Простенки тоже не извлечены. Передайте лимиты явно или housingType=mgn.",
              },
            ],
            isError: true,
          };
        }

        const params = {
          minBalconyWidthMm: limits.minBalconyWidthMm,
          minLoggiaWidthMm: limits.minLoggiaWidthMm,
          minLoggiaDepthMm: limits.minLoggiaDepthMm,
          minFirePierToOpeningMm: limits.minFirePierToOpeningMm,
          minFirePierBetweenOpeningsMm: limits.minFirePierBetweenOpeningsMm,
          mode: args.mode ?? "report",
          levelName: args.levelName ?? "",
          roomNameFilter: args.roomNameFilter ?? "",
          includeCompliant: args.includeCompliant ?? false,
          checkFirePiers: args.checkFirePiers ?? true,
          highlightColor: args.highlightColor,
        };

        const rawResponse = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("check_min_dimensions", params);
        });

        const raw = rawCheckResultSchema.parse(rawResponse);
        if (!raw.success) {
          return {
            content: [
              {
                type: "text",
                text: raw.message || "check_min_dimensions failed.",
              },
            ],
            isError: true,
          };
        }

        const pdfFiles =
          args.normativePdfFiles ??
          (args.scanAllNormativePdfs ? ["*"] : [...DEFAULT_MIN_DIMENSIONS_PDF_FILES]);

        const result: CheckMinDimensionsResult = {
          success: true,
          message: raw.message,
          mode: raw.mode,
          limits,
          totalSpacesChecked: raw.totalSpacesChecked ?? 0,
          violationCount: raw.violationCount ?? raw.violations?.length ?? 0,
          violations: raw.violations ?? [],
          compliantItems: raw.compliantItems ?? [],
          highlightedCount: raw.highlightedCount,
          normativesDir,
          normativePdfFiles: pdfFiles,
          availableRules: rules,
          warnings,
        };

        const report = formatMinDimensionsReport(result);

        return {
          content: [
            { type: "text", text: report },
            {
              type: "text",
              text: JSON.stringify(
                {
                  norm: {
                    limits,
                    source: args.source ?? limits.appliedRules[0]?.source ?? null,
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
              text: `check_min_dimensions failed: ${
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
