/**
 * REV-155: Revit stops collecting at `limit` segments and only then is the layer filter
 * applied to what it managed to read. On a large DWG that silently drops whole layers:
 * on «двг2.dwg» (19 011 segments) the default limit of 5 000 made
 * trace_columns_from_cad find 21 of 49 columns — and report success — because the
 * right-hand half of the building was never read. get_cad_link_geometry had the same
 * hole: its per-layer table is built from the truncated set, so the wall layer
 * (A-WALL-____-MCUT, 1 031 segments) did not appear at all and the obvious next step
 * was to conclude the DWG had no walls.
 *
 * The response already carries `truncated`; nothing read it. Re-read with a bigger
 * limit while the drawing says there is more.
 */

export type TruncatableResponse = {
  truncated?: boolean;
  count?: number;
  items?: unknown[];
  [key: string]: unknown;
};

export const DEFAULT_MAX_ESCALATION_LIMIT = 60000;

export type EscalationResult<T> = {
  response: T;
  /** Limit that produced `response`. */
  limitUsed: number;
  /** Number of extra reads spent escalating. */
  rereads: number;
  /** True when the DWG is still larger than `maxLimit` — the read really is partial. */
  stillTruncated: boolean;
};

/**
 * Call `fetch` with a growing limit until the response is no longer truncated.
 *
 * @param fetch     reads the CAD geometry at a given limit
 * @param startLimit limit requested by the caller
 * @param maxLimit  ceiling, so a pathological DWG cannot loop forever
 */
export async function readWithLimitEscalation<T extends TruncatableResponse>(
  fetch: (limit: number) => Promise<T>,
  startLimit: number,
  maxLimit: number = DEFAULT_MAX_ESCALATION_LIMIT
): Promise<EscalationResult<T>> {
  const start = startLimit > 0 ? startLimit : 5000;
  let limit = Math.min(start, maxLimit);
  let response = await fetch(limit);
  let rereads = 0;

  while (response?.truncated === true && limit < maxLimit) {
    limit = Math.min(limit * 4, maxLimit);
    response = await fetch(limit);
    rereads++;
  }

  return {
    response,
    limitUsed: limit,
    rereads,
    stillTruncated: response?.truncated === true,
  };
}

/**
 * Warning to surface when even `maxLimit` was not enough — the caller is looking at a
 * partial drawing and needs to narrow layerFilter or raise limit by hand.
 */
export function truncationWarning(
  result: EscalationResult<TruncatableResponse>
): string | null {
  if (!result.stillTruncated) return null;
  return (
    `DWG крупнее лимита чтения: прочитано ${result.limitUsed} сегментов, в чертеже есть ещё. ` +
    "Слои и геометрия за пределами лимита НЕ учтены — сузьте layerFilter или поднимите limit."
  );
}
