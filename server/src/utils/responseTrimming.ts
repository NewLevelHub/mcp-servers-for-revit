/**
 * Trimming for Revit payloads that are correct but far too large for a chat turn
 * (REV-42).
 *
 * Measured on «Короткий блок» (572 rooms), from the metrics log of 14–17 Aug 2026:
 * `export_room_data` answered **314 743 B** and was called 66 times — one call cost
 * the model more context than the entire 92-tool catalog. `get_document_styles`
 * answered 158 462 B. Schedule exports were fixed this way back in REV-6; these
 * were not.
 *
 * As in REV-6, trimming happens **on the MCP server, after Revit has answered**.
 * Revit does the same work either way, so this buys no Revit time — it keeps the
 * chat from drowning. Internal callers that need every field
 * (`normAudit/auditSnapshot.ts`, `normAudit/runners.ts`) go straight to
 * `sendCommand` and never pass through here, which is exactly why the tool layer
 * can afford a compact default.
 */

/** Row objects keyed by field name, as they arrive from the C# models. */
type Row = Record<string, unknown>;

function isRow(value: unknown): value is Row {
  return !!value && typeof value === "object" && !Array.isArray(value);
}

/** `["all"]` — or any casing of it — means "do not project". */
export function wantsAllFields(fields: readonly string[] | undefined): boolean {
  return !!fields?.some((field) => field.trim().toLowerCase() === "all");
}

/**
 * Keep only `fields` on each row. Unknown names are ignored rather than
 * producing `undefined` keys, and a row that has none of them is returned whole:
 * an empty object tells the model nothing, and silently dropping data is worse
 * than sending a little too much.
 */
export function projectRowFields(rows: readonly unknown[], fields: readonly string[]): unknown[] {
  const wanted = fields.map((field) => field.trim()).filter(Boolean);
  if (wanted.length === 0) return [...rows];

  return rows.map((row) => {
    if (!isRow(row)) return row;

    const projected: Row = {};
    for (const field of wanted) {
      if (field in row) projected[field] = row[field];
    }
    return Object.keys(projected).length > 0 ? projected : row;
  });
}

export interface PaginateRowsOptions {
  /** Property holding the row array, e.g. `"rooms"`. */
  key: string;
  offset?: number;
  limit?: number;
  /** Field projection; omit or pass `["all"]` to keep every field. */
  fields?: readonly string[];
}

/**
 * Page and project one array inside a result object, and describe what was done
 * under `<key>Pagination` so the model can fetch the rest without guessing.
 *
 * Returns the response untouched when it is not an object or the key holds no
 * array — a shape this does not recognise is a shape it must not mangle.
 */
export function paginateRows(response: unknown, options: PaginateRowsOptions): unknown {
  if (!isRow(response)) return response;

  const rows = response[options.key];
  if (!Array.isArray(rows)) return response;

  const total = rows.length;
  const offset = Math.max(0, Math.trunc(options.offset ?? 0));
  // No limit given means "the whole array" — but never 0, which would page nothing.
  const limit = Math.max(1, Math.trunc(options.limit ?? Math.max(total, 1)));

  const page = rows.slice(offset, offset + limit);
  const projectAll = wantsAllFields(options.fields) || !options.fields?.length;
  const projected = projectAll ? page : projectRowFields(page, options.fields!);

  const pagination: Row = {
    total,
    offset,
    limit,
    returned: projected.length,
    hasMore: offset + projected.length < total,
  };

  if (!projectAll) {
    pagination.fields = options.fields!.map((field) => field.trim()).filter(Boolean);
    pagination.note =
      `Показаны только поля ${pagination.fields as string[]}. ` +
      `Передай fields:["all"], если нужны остальные.`;
  }
  if (pagination.hasMore) {
    pagination.nextOffset = offset + projected.length;
  }

  return {
    ...response,
    [options.key]: projected,
    [`${options.key}Pagination`]: pagination,
  };
}

/**
 * Keep only entries whose `name` contains `needle`, in every top-level array.
 *
 * Capping a list the model searches by name is only safe if it can also search
 * it: `get_document_styles` exists so the model can find "the ADSK 2.5 mm text
 * type" and pass that exact name to a create_* call. Cap without search and it
 * invents a name instead — the failure the whole tool was written to prevent.
 *
 * Entries with no `name` are dropped from a filtered list: they cannot match, and
 * keeping them would dilute the answer.
 */
export function filterListsByName(response: unknown, needle: string): unknown {
  if (!isRow(response)) return response;

  const query = needle.trim().toLowerCase();
  if (!query) return response;

  const result: Row = { ...response };
  for (const [key, value] of Object.entries(result)) {
    if (!Array.isArray(value)) continue;
    result[key] = value.filter((entry) => {
      if (!isRow(entry)) return false;
      const name = entry.name;
      return typeof name === "string" && name.toLowerCase().includes(query);
    });
  }

  result.nameFilter = needle.trim();
  return result;
}

export interface CapListsOptions {
  /** Max entries kept per array. */
  limit: number;
  /** Arrays to leave alone (short lists that the model needs whole). */
  keep?: readonly string[];
  /** Appended to the truncation note — how to narrow instead of paging. */
  narrowHint?: string;
}

/**
 * Cap every top-level array in a result object, recording the original length.
 *
 * For responses that are several parallel lists rather than one long one —
 * `get_document_styles` returns eight, and the fill-pattern list alone runs to
 * hundreds of hatches nobody reads. Capping each one keeps the *shape* intact, so
 * the model still learns which kinds of style exist and can ask for more.
 */
export function capLists(response: unknown, options: CapListsOptions): unknown {
  if (!isRow(response)) return response;

  const limit = Math.max(0, Math.trunc(options.limit));
  const keep = new Set(options.keep ?? []);
  const result: Row = { ...response };
  const capped: Record<string, number> = {};

  for (const [key, value] of Object.entries(result)) {
    if (!Array.isArray(value) || keep.has(key) || value.length <= limit) continue;
    result[key] = value.slice(0, limit);
    capped[key] = value.length;
  }

  if (Object.keys(capped).length > 0) {
    result.listsTruncated = {
      limit,
      totals: capped,
      note:
        "Списки обрезаны до limit; в totals — исходная длина каждого. " +
        "Нужного имени не видно — не выдумывай его: " +
        (options.narrowHint ?? "повтори вызов с большим listLimit."),
    };
  }

  return result;
}
