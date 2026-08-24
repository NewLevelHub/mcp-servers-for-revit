import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { snapshotDb, snapshotDbPath } from "../database/snapshotDb.js";
import {
  beginSnapshot,
  deleteSnapshot,
  finishSnapshot,
  findSnapshotByLabel,
  getSnapshot,
  insertSnapshotElements,
  listSnapshots,
  pruneSnapshots,
  snapshotCategoryBreakdown,
  snapshotLevelBreakdown,
  type SnapshotHeader,
} from "../database/snapshots.js";
import {
  defaultSnapshotLabel,
  toSnapshotRows,
  type RawSnapshotElement,
} from "../utils/modelSnapshot.js";

/** One page as `export_model_snapshot` answers. */
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
  elapsedMs?: number;
  scanElapsedMs?: number;
  elements?: RawSnapshotElement[];
}

/**
 * Pages a snapshot is read in.
 *
 * Small enough that no page comes near the 50 MB socket frame, large enough that
 * a 300k-element model is sixty round trips and not three thousand. Each page is
 * one external event in Revit, so the count is what the fixed cost per page
 * multiplies by.
 */
const DEFAULT_BATCH_SIZE = 5000;

/** Snapshots of one model kept before the oldest are dropped. */
const DEFAULT_KEEP_PER_MODEL = 5;

/** A run that needs more pages than this is refused rather than left to spin. */
const MAX_PAGES = 400;

function formatHeader(snapshot: SnapshotHeader) {
  return {
    id: snapshot.id,
    label: snapshot.label,
    model: snapshot.modelName,
    takenAt: new Date(snapshot.takenAt).toISOString(),
    elements: snapshot.elementCount,
    durationMs: snapshot.durationMs,
    status: snapshot.status,
    note: snapshot.note ?? undefined,
  };
}

export function registerCreateModelSnapshotTool(server: McpServer) {
  server.tool(
    "create_model_snapshot",
    "Снимок состояния открытой модели в базу — основа для сравнения версий: «что изменилось " +
      "с прошлой выдачи». Records every model element with its ElementId and UniqueId, category, " +
      "type, level, room, bounding box and a hash of its key parameters, then stores it under a " +
      "name («выдача АР 19.08.2026»). Nothing is written into the Revit model — this is a read " +
      "there and a write into our own SQLite database. " +
      "Take a snapshot BEFORE the model changes: a comparison needs the earlier state to have " +
      "been recorded, and there is no way to recover it afterwards. " +
      "The same tool also lists snapshots (action: \"list\") and deletes them (action: \"delete\") — " +
      "a snapshot of a large model is hundreds of megabytes, and old выдачи are dropped " +
      "automatically past keepPerModel. " +
      "Taking a snapshot of a large model takes minutes and is read in pages; do not call it " +
      "again while one is running. " +
      "Snapshots are kept outside the add-in folder, so updating the plugin does not lose them. " +
      "For «сколько элементов в модели сейчас» use analyze_model_statistics instead — it answers " +
      "in seconds and writes nothing. " +
      "To read what changed since a snapshot was taken — «что изменилось с прошлой выдачи» — use " +
      "compare_model_versions; it takes the snapshot from here and either a second one or the " +
      "model as it is open right now.",
    {
      action: z
        .enum(["create", "list", "delete"])
        .optional()
        .default("create")
        .describe(
          "create — снять снимок открытой модели (по умолчанию); list — перечислить снимки; " +
            "delete — удалить снимок по snapshotId или по label."
        ),
      label: z
        .string()
        .optional()
        .describe(
          "Имя снимка, как его назовёт архитектор: «выдача АР 19.08.2026». Повторный снимок с " +
            "тем же именем по той же модели ЗАМЕЩАЕТ прежний, а не кладётся рядом. По умолчанию — " +
            "«снимок ДД.ММ.ГГГГ ЧЧ:ММ». Для action: \"delete\" — какой снимок удалить."
        ),
      note: z
        .string()
        .optional()
        .describe("Пометка к снимку: чем эта выдача отличается, что в ней проверялось."),
      modelName: z
        .string()
        .optional()
        .describe(
          "Имя модели для list и delete. По умолчанию list показывает снимки всех моделей, а " +
            "delete по label ищет среди всех и отказывается удалять, если такое имя занято в " +
            "нескольких моделях."
        ),
      snapshotId: z
        .number()
        .int()
        .optional()
        .describe("Какой снимок удалить — точнее, чем label. Берётся из ответа list."),
      categories: z
        .array(z.string())
        .optional()
        .describe(
          "Снять только эти категории — по-русски, по-английски или как BuiltInCategory " +
            "(«Стены», \"Walls\", \"OST_Walls\"). По умолчанию снимается вся модель; сужать стоит " +
            "только когда сравнивать заведомо нужно одну категорию."
        ),
      extraParameters: z
        .array(z.string())
        .optional()
        .describe(
          "Имена параметров сверх ключевого набора — ADSK_, параметры проекта организации. " +
            "Ключевой набор (марка, стадия, уровень и смещения, габаритные размеры, площади " +
            "помещений) снимается всегда; каждый лишний параметр — это чтение на каждый элемент."
        ),
      includeAnnotation: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Включить аннотации и элементы вида (размеры, марки, узлы). По умолчанию выключено: " +
            "перечерченный на листе размер — не изменение здания, а на оформленной модели таких " +
            "элементов больше, чем самой модели."
        ),
      includeRooms: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Записывать, в каком помещении стоит элемент. Нужно, чтобы сравнение говорило «в кв. 45», " +
            "а не «на 3 этаже»; выключается, если снимок оказался слишком долгим."
        ),
      includeBoundingBox: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Записывать габарит элемента в мм. Без него переставленная стена читается как " +
            "«удалена и добавлена», а не как перемещённая."
        ),
      includeServiceCategories: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Включить служебные категории: эскизные линии перекрытий и лестниц, материалы, " +
            "наборы характеристик, листы, компоненты легенды, камеры, траекторию солнца. " +
            "По умолчанию выключено — на реальной модели это 38 % снимка, и правка одного " +
            "перекрытия приходит как тридцать изменённых линий эскиза вместо одного " +
            "изменённого перекрытия."
        ),
      batchSize: z
        .number()
        .int()
        .min(200)
        .max(20000)
        .optional()
        .default(DEFAULT_BATCH_SIZE)
        .describe(
          "Сколько элементов читать за один заход (по умолчанию 5000). Меньше — дольше, больше — " +
            "тяжелее один ответ."
        ),
      keepPerModel: z
        .number()
        .int()
        .min(1)
        .max(50)
        .optional()
        .default(DEFAULT_KEEP_PER_MODEL)
        .describe(
          "Сколько снимков модели хранить. Старшие удаляются после успешного снятия нового."
        ),
    },
    async (args) => {
      try {
        if (args.action === "list") {
          const snapshots = listSnapshots(snapshotDb(), args.modelName);
          return ok({
            action: "list",
            total: snapshots.length,
            // Where they physically are. Worth saying: snapshots live in the user
            // profile rather than under the add-in, precisely so an update cannot
            // take them (REV-170).
            database: snapshotDbPath(),
            snapshots: snapshots.map(formatHeader),
          });
        }

        if (args.action === "delete") {
          return handleDelete(args);
        }

        return await handleCreate(args);
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `create_model_snapshot не выполнен: ${
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

function ok(payload: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(payload) }] };
}

function failed(message: string) {
  return {
    content: [{ type: "text" as const, text: JSON.stringify({ success: false, message }) }],
    isError: true,
  };
}

/**
 * Deleting never touches Revit. A snapshot is ours, not the model's, and the
 * moment someone wants the disk back is exactly the moment the model may not be
 * open — asking Revit which file that is would turn a housekeeping call into a
 * failure.
 */
function handleDelete(args: { snapshotId?: number; label?: string; modelName?: string }) {
  let target: SnapshotHeader | null = null;

  if (typeof args.snapshotId === "number") {
    target = getSnapshot(snapshotDb(), args.snapshotId);
  } else if (args.label && args.modelName) {
    target = findSnapshotByLabel(snapshotDb(), args.modelName, args.label);
  } else if (args.label) {
    const matches = listSnapshots(snapshotDb()).filter((snapshot) => snapshot.label === args.label);

    if (matches.length > 1) {
      return failed(
        `Снимков с именем «${args.label}» несколько — по моделям: ` +
          `${matches.map((snapshot) => snapshot.modelName).join(", ")}. ` +
          "Добавьте modelName или удаляйте по snapshotId."
      );
    }

    target = matches[0] ?? null;
  } else {
    return failed("Нечего удалять: укажите snapshotId или label.");
  }

  if (!target) {
    return failed('Такого снимка нет. Список снятых снимков — action: "list".');
  }

  deleteSnapshot(snapshotDb(), target.id);
  return ok({
    action: "delete",
    deleted: formatHeader(target),
    message: `Снимок «${target.label}» удалён (${target.elementCount} элементов).`,
  });
}

async function handleCreate(args: {
  label?: string;
  note?: string;
  categories?: string[];
  extraParameters?: string[];
  includeAnnotation?: boolean;
  includeRooms?: boolean;
  includeBoundingBox?: boolean;
  includeServiceCategories?: boolean;
  batchSize?: number;
  keepPerModel?: number;
}) {
  const batchSize = args.batchSize ?? DEFAULT_BATCH_SIZE;
  const keepPerModel = args.keepPerModel ?? DEFAULT_KEEP_PER_MODEL;
  const label = args.label?.trim() || defaultSnapshotLabel();

  const readPage = (offset: number, snapshotToken: string) =>
    withRevitConnection((client) =>
      client.sendCommand("export_model_snapshot", {
        offset,
        limit: batchSize,
        includeAnnotation: args.includeAnnotation ?? false,
        includeRooms: args.includeRooms ?? true,
        includeBoundingBox: args.includeBoundingBox ?? true,
        includeServiceCategories: args.includeServiceCategories ?? false,
        ...(args.categories?.length ? { categories: args.categories } : {}),
        ...(args.extraParameters?.length ? { extraParameters: args.extraParameters } : {}),
        ...(snapshotToken ? { snapshotToken } : {}),
      })
    ) as Promise<SnapshotPage>;

  const started = Date.now();
  const first = await readPage(0, "");

  if (first?.success === false) {
    return failed(first.message || "Плагин не смог прочитать модель.");
  }
  if (!first?.modelName) {
    return failed("Revit не сообщил, какая модель открыта — снимок не привязать к файлу.");
  }

  const total = first.totalElements ?? 0;
  if (total === 0) {
    return failed(
      "В открытой модели нет элементов, которые попадают в снимок. Если снимались только " +
        "отдельные категории, проверьте их названия в ответе плагина."
    );
  }

  const pages = Math.ceil(total / batchSize);
  if (pages > MAX_PAGES) {
    return failed(
      `Снимок потребовал бы ${pages} заходов при batchSize=${batchSize}. Увеличьте batchSize ` +
        `или сузьте categories — иначе снятие займёт часы.`
    );
  }

  const { id: snapshotId, replaced } = beginSnapshot(snapshotDb(), {
    modelName: first.modelName,
    modelPath: first.modelPath,
    label,
    note: args.note,
    revitVersion: first.revitVersion,
  });

  const parameterLabels: Record<string, string> = { ...(first.parameterLabels ?? {}) };
  const token = first.snapshotToken ?? "";
  const warnings: string[] = [];

  let written = insertSnapshotElements(snapshotDb(), snapshotId, toSnapshotRows(first.elements ?? []));
  let offset = first.count ?? 0;
  let revitMs = first.elapsedMs ?? 0;
  let pagesRead = 1;

  while (offset < total && pagesRead < MAX_PAGES) {
    const page = await readPage(offset, token);

    if (page?.success === false) {
      warnings.push(
        `Чтение прервалось на ${offset}-м элементе: ${page.message ?? "плагин вернул ошибку"}.`
      );
      break;
    }

    // The element list moved under us — someone edited the model while it was
    // being read. Half of one model plus half of another is not a snapshot of
    // anything, so the run stops here and says so.
    if (page?.snapshotToken && token && page.snapshotToken !== token) {
      warnings.push(
        "Модель изменилась во время снятия снимка — дальше читать нечего, снимок неполный. " +
          "Снимите его заново, ничего не трогая в Revit."
      );
      break;
    }

    const rows = toSnapshotRows(page?.elements ?? []);
    if (rows.length === 0 && (page?.count ?? 0) === 0) {
      warnings.push(`Плагин вернул пустую страницу на ${offset}-м элементе — чтение остановлено.`);
      break;
    }

    written += insertSnapshotElements(snapshotDb(), snapshotId, rows);
    Object.assign(parameterLabels, page?.parameterLabels ?? {});
    offset += page?.count ?? rows.length;
    revitMs += page?.elapsedMs ?? 0;
    pagesRead += 1;
  }

  const durationMs = Date.now() - started;
  const complete = warnings.length === 0 && offset >= total;
  const stored = finishSnapshot(snapshotDb(), snapshotId, {
    durationMs,
    parameterLabels,
    status: complete ? "ready" : "partial",
  });
  // Nothing is pruned after an incomplete run: the older снимки may be the only
  // complete ones left, and dropping them to make room for a short one would
  // destroy the very thing the next comparison needs.
  const pruned = complete ? pruneSnapshots(snapshotDb(), first.modelName, keepPerModel) : [];

  return ok({
    // Not `true` unless the whole model was read. A partial snapshot compares as
    // though everything missing from it had been added since, so the caller has
    // to be told — `normalizeToolResult` turns this into isError, which is the
    // point (см. историю «инструменты рапортуют успех там, где ничего не сделали»).
    success: complete,
    status: complete ? "ready" : "partial",
    action: "create",
    snapshotId,
    label,
    replaced,
    model: first.modelName,
    modelPath: first.modelPath,
    revitVersion: first.revitVersion,
    elementsInModel: total,
    elementsStored: stored,
    // Written and stored differ only when a page repeated a UniqueId — a fact
    // worth seeing rather than a number worth hiding.
    rowsWritten: written,
    pagesRead,
    batchSize,
    durationMs,
    revitMs,
    scanMs: first.scanElapsedMs ?? 0,
    elementsPerSecond: durationMs > 0 ? Math.round((stored / durationMs) * 1000) : null,
    parameterKeys: Object.keys(parameterLabels).length,
    categories: snapshotCategoryBreakdown(snapshotDb(), snapshotId),
    levels: snapshotLevelBreakdown(snapshotDb(), snapshotId),
    prunedSnapshots: pruned.map((snapshot) => snapshot.label),
    warnings,
    message: warnings.length
      ? `Снимок «${label}» снят не полностью: ${stored} из ${total} элементов.`
      : `Снимок «${label}» снят: ${stored} элементов за ${(durationMs / 1000).toFixed(1)} с.`,
  });
}
