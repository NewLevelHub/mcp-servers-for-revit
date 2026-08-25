import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { paginateRows } from "../utils/responseTrimming.js";
import { explainWarning } from "../quality/warningCatalog.js";

interface RawWarningGroup {
  description: string;
  failureDefinitionGuid: string;
  severity: string;
  count: number;
  elementCount: number;
  categories: string[];
  elementIds: number[];
  elementIdsTruncated?: boolean;
}

export function registerExplainModelWarningsTool(server: McpServer) {
  server.tool(
    "explain_model_warnings",
    "get_model_warnings, but read for a ГАП instead of a log: each warning group comes back with " +
      "a plain-language explanation — what it risks at issue time, then what to do — and sorted by " +
      "actual danger, not raw occurrence count. Duplicate/drifted rooms and silent geometry mistakes " +
      "lead; the everyday wall-overlap-at-a-corner warning (often thousands of occurrences, almost " +
      "always harmless) sorts last. `autoFixable` marks the one class this repo can safely fix by " +
      "itself so far (a room-separation line made redundant by a wall already on top of it) — apply " +
      "it with fix_redundant_room_separators, which always previews before it changes anything. " +
      "Every other warning is explain-only: `dangerRank`/`autoFixable` are read-only classification, " +
      "not a to-do list this tool acts on.",
    {
      severity: z
        .enum(["Warning", "Error", "DocumentCorruption"])
        .optional()
        .describe("Keep only this severity. Omit for everything."),
      offset: z
        .number()
        .int()
        .min(0)
        .optional()
        .default(0)
        .describe("Warning groups to skip. Use groupsPagination.nextOffset from the previous call."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(200)
        .optional()
        .default(15)
        .describe(
          "Max warning groups per call (default 15 — the ticket's own bar: 500 raw warnings should " +
            "fold into at most this many explained groups). totalWarnings/totalGroups describe the whole model."
        ),
    },
    async (args) => {
      try {
        const raw = (await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_model_warnings", {
            maxElementIdsPerGroup: 5,
            ...(args.severity ? { severity: args.severity } : {}),
          });
        })) as { totalWarnings: number; totalGroups: number; errorCount: number; groups: RawWarningGroup[] };

        const explained = (raw.groups ?? [])
          .map((g) => {
            const explanation = explainWarning(g.failureDefinitionGuid, g.description);
            return {
              ...explanation,
              rawDescription: g.description,
              severity: g.severity,
              count: g.count,
              elementCount: g.elementCount,
              categories: g.categories,
              elementIds: g.elementIds,
              elementIdsTruncated: g.elementIdsTruncated,
            };
          })
          // REV-180: danger first, occurrence count only breaks ties within the same danger tier.
          .sort((a, b) => a.dangerRank - b.dangerRank || b.count - a.count);

        const trimmed = paginateRows(
          { groups: explained },
          { key: "groups", offset: args.offset ?? 0, limit: args.limit ?? 15, fields: ["all"] }
        );

        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({
                totalWarnings: raw.totalWarnings,
                totalGroups: raw.totalGroups,
                errorCount: raw.errorCount,
                ...(trimmed as object),
              }),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `explain_model_warnings не выполнен: ${
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
