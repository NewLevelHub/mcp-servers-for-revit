import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { snapshotDb } from "../database/snapshotDb.js";
import {
  getSnapshot,
  getSnapshotElements,
  getSnapshotParameterLabels,
  findSnapshotByLabel,
  listSnapshots,
  type SnapshotHeader,
} from "../database/snapshots.js";
import {
  buildDiffHeadline,
  countChanges,
  describeChange,
  diffSnapshotElements,
  groupChanges,
  DEFAULT_MOVE_TOLERANCE_MM,
  type ElementChange,
} from "../utils/modelDiff.js";
import { toSnapshotRows, type RawSnapshotElement } from "../utils/modelSnapshot.js";

/**
 * «Что изменилось с прошлой выдачи», человеческим языком (REV-171).
 *
 * The comparison itself — matching elements, deciding what counts as a change,
 * grouping and wording the result — lives in `utils/modelDiff.ts` and is tested
 * there without Revit. This file only gets the two sides onto the table: rows
 * already in `model_snapshots` for a stored снимок, or a fresh paged read of
 * whatever model is open in Revit right now for "текущее состояние".
 */

/** One page as `export_model_snapshot` answers — same shape create_model_snapshot reads. */
interface SnapshotPage {
  success?: boolean;
  message?: string;
  modelName?: string;
  modelPath?: string;
  revitVersion?: string;
  totalElements?: number;
  offset?: number;
  count?: number;
  hasMore?: boolean;
  snapshotToken?: string;
  parameterLabels?: Record<string, string>;
  elements?: RawSnapshotElement[];
}

const DEFAULT_BATCH_SIZE = 5000;
const MAX_PAGES = 400;

const DEFAULT_LIMIT = 200;
const MAX_LIMIT = 2000;

interface SnapshotSide {
  header: SnapshotHeader;
  rows: ReturnType<typeof getSnapshotElements>;
  labels: Record<string, string>;
}

function formatHeaderRef(header: SnapshotHeader) {
  return {
    id: header.id,
    label: header.label,
    model: header.modelName,
    takenAt: new Date(header.takenAt).toISOString(),
    elements: header.elementCount,
    status: header.status,
  };
}

function failed(message: string) {
  return {
    content: [{ type: "text" as const, text: JSON.stringify({ success: false, message }) }],
    isError: true,
  };
}

function ok(payload: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(payload) }] };
}

/** Same label-resolution rule as `create_model_snapshot`'s delete — id first, then label, disambiguated by model. */
function resolveSnapshot(
  db: ReturnType<typeof snapshotDb>,
  args: { snapshotId?: number; label?: string; modelName?: string },
  argName: string
): SnapshotHeader | { error: string } {
  if (typeof args.snapshotId === "number") {
    const header = getSnapshot(db, args.snapshotId);
    return header ?? { error: `Снимка с id ${args.snapshotId} нет.` };
  }

  if (args.label && args.modelName) {
    const header = findSnapshotByLabel(db, args.modelName, args.label);
    return header ?? { error: `Снимка «${args.label}» по модели «${args.modelName}» нет.` };
  }

  if (args.label) {
    const matches = listSnapshots(db).filter((snapshot) => snapshot.label === args.label);
    if (matches.length > 1) {
      return {
        error:
          `Снимков с именем «${args.label}» несколько — по моделям: ` +
          `${matches.map((snapshot) => snapshot.modelName).join(", ")}. Добавьте modelName.`,
      };
    }
    return matches[0] ?? { error: `Снимка «${args.label}» нет. Список снятых снимков — create_model_snapshot с action: "list".` };
  }

  return { error: `Не указано, какой снимок брать за ${argName}: нужен snapshotId или label.` };
}

function loadStoredSide(db: ReturnType<typeof snapshotDb>, header: SnapshotHeader): SnapshotSide {
  return {
    header,
    rows: getSnapshotElements(db, header.id),
    labels: getSnapshotParameterLabels(db, header.id),
  };
}

/**
 * A fresh, unstored read of whatever model is open in Revit — «текущее
 * состояние». Paged the same way `create_model_snapshot` reads a model, but
 * nothing is written to disk: a comparison does not need a permanent record of
 * "right now", only of the выдачи that get named.
 */
async function readLiveModel(args: {
  batchSize: number;
  categories?: string[];
  extraParameters?: string[];
  includeAnnotation: boolean;
  includeRooms: boolean;
  includeServiceCategories: boolean;
}): Promise<{ header: Omit<SnapshotHeader, "id" | "durationMs">; rows: RawSnapshotElement[]; labels: Record<string, string> } | { error: string }> {
  const readPage = (offset: number, snapshotToken: string) =>
    withRevitConnection((client) =>
      client.sendCommand("export_model_snapshot", {
        offset,
        limit: args.batchSize,
        includeAnnotation: args.includeAnnotation,
        includeRooms: args.includeRooms,
        includeBoundingBox: true,
        includeServiceCategories: args.includeServiceCategories,
        ...(args.categories?.length ? { categories: args.categories } : {}),
        ...(args.extraParameters?.length ? { extraParameters: args.extraParameters } : {}),
        ...(snapshotToken ? { snapshotToken } : {}),
      })
    ) as Promise<SnapshotPage>;

  const first = await readPage(0, "");
  if (first?.success === false) return { error: first.message || "Плагин не смог прочитать модель." };
  if (!first?.modelName) return { error: "Revit не сообщил, какая модель открыта." };

  const total = first.totalElements ?? 0;
  const pages = Math.ceil(total / args.batchSize);
  if (pages > MAX_PAGES) {
    return {
      error: `Модель потребовала бы ${pages} заходов при batchSize=${args.batchSize}. Увеличьте batchSize.`,
    };
  }

  const elements: RawSnapshotElement[] = [...(first.elements ?? [])];
  const labels: Record<string, string> = { ...(first.parameterLabels ?? {}) };
  const token = first.snapshotToken ?? "";
  let offset = first.count ?? elements.length;
  let pagesRead = 1;

  while (offset < total && pagesRead < MAX_PAGES) {
    const page = await readPage(offset, token);
    if (page?.success === false) {
      return { error: `Чтение модели прервалось на ${offset}-м элементе: ${page.message ?? "плагин вернул ошибку"}.` };
    }
    if (page?.snapshotToken && token && page.snapshotToken !== token) {
      return { error: "Модель изменилась во время чтения — сравнение недостоверно. Повторите, ничего не трогая в Revit." };
    }

    elements.push(...(page?.elements ?? []));
    Object.assign(labels, page?.parameterLabels ?? {});
    offset += page?.count ?? 0;
    pagesRead += 1;
  }

  return {
    header: {
      modelName: first.modelName,
      modelPath: first.modelPath ?? null,
      label: "текущее состояние",
      note: null,
      takenAt: Date.now(),
      elementCount: elements.length,
      revitVersion: first.revitVersion ?? null,
      status: offset >= total ? "ready" : "partial",
    },
    rows: elements,
    labels,
  };
}

export function registerCompareModelVersionsTool(server: McpServer) {
  server.tool(
    "compare_model_versions",
    "«Что изменилось с прошлой выдачи» человеческим языком — сравнивает сохранённый снимок " +
      "(create_model_snapshot) с текущим состоянием открытой модели, либо два сохранённых снимка " +
      "между собой. Отвечает так, как спросил бы ГАП: сводка сверху («переставлено 12 стен на " +
      "3 этаже, площадь пом. 45 выросла на 4 м²»), затем список добавленного/удалённого/изменённого, " +
      "сгруппированный по уровням и помещениям. " +
      "Переставленный элемент виден как ОДНО изменение положения, а не как удаление плюс добавление " +
      "— пары находятся по UniqueId, который Revit не меняет, пока элемент не удалён и не создан " +
      "заново. Автоматически пересчитанные Revit'ом параметры (площадь и объём перекрытий, длина " +
      "кривых, периметр помещения) в diff не попадают — иначе список нечитаем; площадь самого " +
      "помещения (ROOM_AREA) остаётся, это и есть та цифра, ради которой всё затевалось. " +
      "Список может быть длинным — используйте offset/limit. " +
      "Ничего не пишет в модель и не создаёт снимков сам — для этого есть create_model_snapshot.",
    {
      fromSnapshotId: z.number().int().optional().describe("Более ранний снимок, по id — из create_model_snapshot (action: \"list\")."),
      fromLabel: z.string().optional().describe("Более ранний снимок, по имени («выдача АР 19.08.2026»)."),
      toSnapshotId: z.number().int().optional().describe("Более поздний снимок, по id. Если не задан ни он, ни toLabel — берётся текущее состояние открытой модели."),
      toLabel: z.string().optional().describe("Более поздний снимок, по имени."),
      modelName: z
        .string()
        .optional()
        .describe("Имя модели — нужно, только если fromLabel/toLabel встречается у нескольких моделей."),
      moveToleranceMm: z
        .number()
        .min(0)
        .max(1000)
        .optional()
        .default(DEFAULT_MOVE_TOLERANCE_MM)
        .describe(`Ниже этого порога (по умолчанию ${DEFAULT_MOVE_TOLERANCE_MM} мм) смещение считается погрешностью пересчёта, а не перестановкой.`),
      allowModelMismatch: z
        .boolean()
        .optional()
        .default(false)
        .describe("Сравнивать, даже если имя модели в снимке и текущей открытой модели не совпадает. По умолчанию отказ — иначе цифры сравнения ничего не значат."),
      offset: z.number().int().min(0).optional().default(0).describe("С какого изменения начать список (после сортировки по уровням/помещениям)."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(MAX_LIMIT)
        .optional()
        .default(DEFAULT_LIMIT)
        .describe(`Сколько изменений вернуть за раз (по умолчанию ${DEFAULT_LIMIT}). Дальше — через offset.`),
      categories: z
        .array(z.string())
        .optional()
        .describe("При чтении текущего состояния — те же категории, что снимались в fromSnapshot; иначе разница в наборе категорий даст ложные добавления/удаления."),
      extraParameters: z.array(z.string()).optional().describe("При чтении текущего состояния — те же extraParameters, что использовал fromSnapshot."),
      includeAnnotation: z.boolean().optional().default(false).describe("При чтении текущего состояния — читать ли аннотации (как в create_model_snapshot)."),
      includeRooms: z.boolean().optional().default(true).describe("При чтении текущего состояния — читать ли принадлежность к помещению."),
      includeServiceCategories: z.boolean().optional().default(false).describe("При чтении текущего состояния — включать ли служебные категории."),
      batchSize: z.number().int().min(200).max(20000).optional().default(DEFAULT_BATCH_SIZE).describe("Размер страницы при чтении текущего состояния."),
    },
    async (args) => {
      try {
        const db = snapshotDb();

        const fromResolved = resolveSnapshot(db, { snapshotId: args.fromSnapshotId, label: args.fromLabel, modelName: args.modelName }, "fromSnapshot");
        if ("error" in fromResolved) return failed(fromResolved.error);
        const from = loadStoredSide(db, fromResolved);

        let to: SnapshotSide | null = null;
        let toIsLive = false;
        const warnings: string[] = [];

        // Not a reason to refuse — the caller already knows from create_model_snapshot's own
        // answer that this снимок is short. But a diff built on it will read every element that
        // snapshot never reached as "added since", so it has to be said again at the moment it
        // actually matters.
        if (from.header.status === "partial") {
          warnings.push(`Снимок «${from.header.label}» снят не полностью — сравнение построено на неполном срезе.`);
        }

        if (typeof args.toSnapshotId === "number" || args.toLabel) {
          const toResolved = resolveSnapshot(db, { snapshotId: args.toSnapshotId, label: args.toLabel, modelName: args.modelName }, "toSnapshot");
          if ("error" in toResolved) return failed(toResolved.error);
          to = loadStoredSide(db, toResolved);
        } else {
          const live = await readLiveModel({
            batchSize: args.batchSize,
            categories: args.categories,
            extraParameters: args.extraParameters,
            includeAnnotation: args.includeAnnotation,
            includeRooms: args.includeRooms,
            includeServiceCategories: args.includeServiceCategories,
          });
          if ("error" in live) return failed(live.error);
          toIsLive = true;
          to = {
            header: { ...live.header, id: -1, durationMs: null },
            rows: toSnapshotRows(live.rows),
            labels: live.labels,
          };
        }

        if (!to) return failed("Не удалось определить, с чем сравнивать.");

        if (from.header.modelName !== to.header.modelName && !args.allowModelMismatch) {
          return failed(
            `Снимок «${from.header.label}» снят с модели «${from.header.modelName}», а ` +
              `${toIsLive ? "сейчас открыта" : "снимок для сравнения снят с"} «${to.header.modelName}». ` +
              "Сравнение двух разных моделей ничего не значит. Откройте нужную модель или передайте allowModelMismatch: true, если это осознанно."
          );
        }

        if (to.header.status === "partial") {
          warnings.push(
            toIsLive
              ? "Чтение текущей модели прервалось — сравнение построено на неполном срезе."
              : `Снимок «${to.header.label}» снят не полностью — сравнение построено на неполном срезе.`
          );
        }

        const parameterLabels = { ...from.labels, ...to.labels };
        const changes = diffSnapshotElements(from.rows, to.rows, parameterLabels, {
          moveToleranceMm: args.moveToleranceMm,
        });

        const counts = countChanges(changes);
        const headline = buildDiffHeadline(changes);
        const allGroups = groupChanges(changes);

        const flatOrdered: ElementChange[] = [];
        for (const level of allGroups) {
          for (const room of level.rooms) flatOrdered.push(...room.changes);
        }

        const offset = args.offset;
        const limit = args.limit;
        const page = flatOrdered.slice(offset, offset + limit);
        const pageIds = new Set(page.map((change) => `${change.kind}:${change.uniqueId}`));

        const groups = allGroups
          .map((level) => ({
            level: level.level || "(без уровня)",
            count: level.count,
            rooms: level.rooms
              .map((room) => ({
                room: room.room || "(вне помещений)",
                count: room.changes.length,
                changes: room.changes
                  .filter((change) => pageIds.has(`${change.kind}:${change.uniqueId}`))
                  .map((change) => ({
                    text: describeChange(change),
                    kind: change.kind,
                    elementId: change.elementId,
                    uniqueId: change.uniqueId,
                    category: change.category,
                    moved: change.moved,
                    moveDistanceMm: change.moveDistanceMm,
                    changedParameters: change.changedParameters,
                  })),
              }))
              .filter((room) => room.changes.length > 0),
          }))
          .filter((level) => level.rooms.length > 0);

        return ok({
          success: to.header.status === "ready" && from.header.status === "ready",
          from: formatHeaderRef(from.header),
          to: toIsLive
            ? { live: true, model: to.header.modelName, takenAt: new Date(to.header.takenAt).toISOString(), elements: to.header.elementCount, status: to.header.status }
            : formatHeaderRef(to.header),
          moveToleranceMm: args.moveToleranceMm,
          headline,
          counts,
          levels: allGroups.map((level) => ({ level: level.level || "(без уровня)", count: level.count })),
          pagination: {
            offset,
            limit,
            totalChanges: flatOrdered.length,
            returned: page.length,
            hasMore: offset + page.length < flatOrdered.length,
          },
          groups,
          warnings,
          message: headline,
        });
      } catch (error) {
        return {
          content: [
            { type: "text" as const, text: `compare_model_versions не выполнен: ${error instanceof Error ? error.message : String(error)}` },
          ],
          isError: true,
        };
      }
    }
  );
}
