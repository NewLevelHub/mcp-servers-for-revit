import { z } from "zod";

/**
 * Shared pagination args for schedule export tools (doors/windows/floors/curtain walls).
 * On large models the full per-instance list (1000+ elements) overloads the chat,
 * so instances are omitted by default and group elementIds are truncated.
 */
export const scheduleExportPaginationSchema = {
  includeInstances: z
    .boolean()
    .optional()
    .default(false)
    .describe(
      "Include the per-instance list. Off by default: groups already carry counts, sizes, and levels; on large models the full list (1000+ elements) overloads the chat. Page with instancesOffset/instancesLimit."
    ),
  instancesOffset: z
    .number()
    .int()
    .min(0)
    .optional()
    .default(0)
    .describe("Pagination offset into the instances list (used with includeInstances)."),
  instancesLimit: z
    .number()
    .int()
    .min(1)
    .max(500)
    .optional()
    .default(100)
    .describe(
      "Max instances returned per call (used with includeInstances). Use instancesOffset to fetch the next page."
    ),
  maxElementIdsPerGroup: z
    .number()
    .int()
    .min(0)
    .max(1000)
    .optional()
    .default(20)
    .describe(
      "Max elementIds kept per group row. Longer lists are truncated; elementIdsTotal keeps the original count."
    ),
};

export interface ScheduleExportPaginationArgs {
  includeInstances?: boolean;
  instancesOffset?: number;
  instancesLimit?: number;
  maxElementIdsPerGroup?: number;
}

/**
 * Trims a ScheduleExportResult-shaped response before it is returned to the chat.
 * The full payload from Revit stays local; only the trimmed view reaches the LLM.
 */
export function paginateScheduleExport(
  response: unknown,
  args: ScheduleExportPaginationArgs
): unknown {
  if (!response || typeof response !== "object" || Array.isArray(response)) {
    return response;
  }

  const includeInstances = args.includeInstances ?? false;
  const offset = Math.max(0, args.instancesOffset ?? 0);
  const limit = Math.max(1, args.instancesLimit ?? 100);
  const maxIdsPerGroup = Math.max(0, args.maxElementIdsPerGroup ?? 20);

  const result: Record<string, unknown> = { ...(response as Record<string, unknown>) };

  if (Array.isArray(result.groups)) {
    result.groups = result.groups.map((group: unknown) => {
      if (
        !group ||
        typeof group !== "object" ||
        !Array.isArray((group as Record<string, unknown>).elementIds)
      ) {
        return group;
      }

      const elementIds = (group as Record<string, unknown>).elementIds as unknown[];
      if (elementIds.length <= maxIdsPerGroup) {
        return group;
      }

      return {
        ...(group as Record<string, unknown>),
        elementIds: elementIds.slice(0, maxIdsPerGroup),
        elementIdsTotal: elementIds.length,
        elementIdsTruncated: true,
      };
    });
  }

  if (Array.isArray(result.instances)) {
    const instances = result.instances as unknown[];
    const total = instances.length;

    if (!includeInstances) {
      delete result.instances;
      result.instancesPagination = {
        total,
        returned: 0,
        note: "Instances omitted. Pass includeInstances=true with instancesOffset/instancesLimit to page through them.",
      };
    } else {
      const page = instances.slice(offset, offset + limit);
      result.instances = page;
      result.instancesPagination = {
        total,
        offset,
        limit,
        returned: page.length,
        hasMore: offset + page.length < total,
      };
    }
  }

  return result;
}
