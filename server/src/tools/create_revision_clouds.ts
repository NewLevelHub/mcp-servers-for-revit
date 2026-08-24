import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  clusterChangeLocations,
  describeCluster,
  DEFAULT_CLOUD_MARGIN_MM,
  DEFAULT_CLUSTER_RADIUS_MM,
  type ChangeLocation,
  type CloudCluster,
} from "../utils/revisionClouds.js";

/**
 * Облака изменений вокруг изменённых зон, из результата compare_model_versions
 * (REV-172).
 *
 * The clustering — which changes fold into one cloud — is `utils/revisionClouds.ts`,
 * pure TypeScript, tested on synthetic points. This file gets that decision onto
 * Revit: create (or reuse) the Revision, find the right view per level, draw one
 * cloud per cluster, skip a cluster whose signature is already there. The plugin
 * command does the Revit-side existence check — it is the only side that can see
 * what is already in the model.
 */

const MAX_CLUSTER_RADIUS_MM = 50000;
const MAX_MARGIN_MM = 10000;

interface CreateRevisionCloudsResponse {
  success?: boolean;
  message?: string;
  revisionId?: number;
  revisionNumber?: number;
  created?: Array<{
    cloudId: number;
    level: string;
    viewName: string;
    sheetNumber?: string;
    sheetName?: string;
    signature: string;
    changeCount: number;
  }>;
  skipped?: Array<{ level: string; signature: string; changeCount: number; reason: string }>;
  warnings?: string[];
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

function clusterForWire(cluster: CloudCluster) {
  return {
    level: cluster.level,
    signature: cluster.signature,
    changeCount: cluster.changeCount,
    minXMm: cluster.cloudBoundsMm.minX,
    minYMm: cluster.cloudBoundsMm.minY,
    maxXMm: cluster.cloudBoundsMm.maxX,
    maxYMm: cluster.cloudBoundsMm.maxY,
    comment: describeCluster(cluster),
  };
}

export function registerCreateRevisionCloudsTool(server: McpServer) {
  server.tool(
    "create_revision_clouds",
    "Облака изменений на нужных видах, из результата compare_model_versions — оформление diff'а, " +
      "а не сам diff. Берёт плоский список изменений с location (compare_model_versions отдаёт его " +
      "на каждом change — соберите groups[].rooms[].changes[] в один массив), кластерует близкие " +
      "изменения в одно облако (радиус настраивается clusterRadiusMm — по умолчанию " +
      `${DEFAULT_CLUSTER_RADIUS_MM} мм, масштаб одной комнаты), находит план нужного уровня, ` +
      "рисует по одному облаку на кластер и заводит (или переиспользует неизданную) Revision с " +
      "заданным описанием. Марка ревизии и попадание листа в таблицу ревизий — штатное поведение " +
      "Revit, как только облако оказалось на виде, размещённом на листе. " +
      "Повторный вызов с тем же diff'ом не плодит дубли: кластер помечается сигнатурой по составу " +
      "изменений, и уже нарисованное облако с той же сигнатурой пропускается, а не рисуется заново. " +
      "dryRun считает кластеры и не трогает Revit — удобно прикинуть, сколько облаков получится, " +
      "прежде чем рисовать.",
    {
      changes: z
        .array(
          z.object({
            elementId: z.number().int(),
            uniqueId: z.string().min(1),
            level: z.string(),
            label: z.string().optional().describe("Короткая подпись для комментария облака — берите text/label из compare_model_versions."),
            location: z
              .object({ x: z.number(), y: z.number(), z: z.number().optional() })
              .nullable()
              .optional()
              .describe("Центр габарита, мм — поле location у каждого change из compare_model_versions."),
          })
        )
        .min(1)
        .describe(
          "Плоский список изменений с координатами. Изменения без location (снимок без габарита) " +
            "пропускаются с предупреждением — их некуда кластеровать."
        ),
      revisionDescription: z
        .string()
        .min(1)
        .describe("Описание ревизии — «Выдача АР 24.08.2026: правки по замечаниям». Существующая неизданная ревизия с тем же описанием переиспользуется, а не дублируется."),
      clusterRadiusMm: z
        .number()
        .min(0)
        .max(MAX_CLUSTER_RADIUS_MM)
        .optional()
        .default(DEFAULT_CLUSTER_RADIUS_MM)
        .describe(`Изменения ближе этого расстояния друг к другу (по цепочке, не только попарно) уходят в одно облако. По умолчанию ${DEFAULT_CLUSTER_RADIUS_MM} мм.`),
      marginMm: z
        .number()
        .min(0)
        .max(MAX_MARGIN_MM)
        .optional()
        .default(DEFAULT_CLOUD_MARGIN_MM)
        .describe(`Насколько облако выступает за габарит кластера. По умолчанию ${DEFAULT_CLOUD_MARGIN_MM} мм.`),
      viewMap: z
        .array(z.object({ level: z.string(), viewName: z.string() }))
        .optional()
        .describe(
          "Явный выбор вида для уровня, когда авто-подбор не устраивает или на уровне несколько " +
            "планов на листах. Без этого плагин берёт план уровня, размещённый на листе; если таких " +
            "несколько или ни одного — предупреждает и выбирает как может."
        ),
      dryRun: z
        .boolean()
        .optional()
        .default(false)
        .describe("Посчитать кластеры, но не создавать ничего в Revit — сколько облаков и где."),
    },
    async (args) => {
      try {
        const located: ChangeLocation[] = [];
        let skippedNoLocation = 0;

        for (const change of args.changes) {
          if (!change.location) {
            skippedNoLocation += 1;
            continue;
          }
          located.push({
            elementId: change.elementId,
            uniqueId: change.uniqueId,
            level: change.level,
            label: change.label ?? "",
            x: change.location.x,
            y: change.location.y,
          });
        }

        if (located.length === 0) {
          return failed(
            skippedNoLocation > 0
              ? `Ни у одного из ${skippedNoLocation} изменений нет location — облака рисовать не с чем.`
              : "Список изменений пуст."
          );
        }

        const clusters = clusterChangeLocations(located, {
          radiusMm: args.clusterRadiusMm,
          marginMm: args.marginMm,
        });

        const clusterSummary = clusters.map((cluster) => ({
          level: cluster.level,
          changeCount: cluster.changeCount,
          comment: describeCluster(cluster),
          boundsMm: cluster.boundsMm,
          signature: cluster.signature,
        }));

        if (args.dryRun) {
          return ok({
            success: true,
            dryRun: true,
            clusterRadiusMm: args.clusterRadiusMm,
            marginMm: args.marginMm,
            clustersCount: clusters.length,
            skippedNoLocation,
            clusters: clusterSummary,
            message: `${clusters.length} облак${clusters.length === 1 ? "о" : ""} по ${located.length} изменени${located.length === 1 ? "ю" : "ям"} — ничего не создано (dryRun).`,
          });
        }

        const response = (await withRevitConnection((client) =>
          client.sendCommand("create_revision_clouds", {
            revisionDescription: args.revisionDescription,
            viewMap: args.viewMap ?? [],
            clusters: clusters.map(clusterForWire),
          })
        )) as CreateRevisionCloudsResponse;

        if (response?.success === false) {
          return failed(response.message || "Плагин не смог создать облака изменений.");
        }

        const created = response?.created ?? [];
        const skipped = response?.skipped ?? [];
        const warnings = [...(response?.warnings ?? [])];
        if (skippedNoLocation > 0) {
          warnings.push(`${skippedNoLocation} изменени${skippedNoLocation === 1 ? "е" : "й"} без location в облака не попали.`);
        }

        return ok({
          success: true,
          revisionId: response?.revisionId,
          revisionNumber: response?.revisionNumber,
          revisionDescription: args.revisionDescription,
          clusterRadiusMm: args.clusterRadiusMm,
          marginMm: args.marginMm,
          clustersRequested: clusters.length,
          created,
          createdCount: created.length,
          skipped,
          skippedCount: skipped.length,
          warnings,
          message:
            `Ревизия №${response?.revisionNumber ?? "?"} «${args.revisionDescription}»: ` +
            `${created.length} облак${created.length === 1 ? "о" : ""} создано, ${skipped.length} уже было.`,
        });
      } catch (error) {
        return {
          content: [
            { type: "text" as const, text: `create_revision_clouds не выполнен: ${error instanceof Error ? error.message : String(error)}` },
          ],
          isError: true,
        };
      }
    }
  );
}
