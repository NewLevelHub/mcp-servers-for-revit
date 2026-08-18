import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { paginateRows } from "../utils/responseTrimming.js";

export function registerGetModelWarningsTool(server: McpServer) {
  server.tool(
    "get_model_warnings",
    "Read Revit's own warning list — the same «Просмотр предупреждений» the architect sees in the ribbon. " +
      "Occurrences are folded by warning text, biggest group first, each with a count, the categories involved " +
      "and a sample of element ids. Use it before issuing a set, when the model behaves oddly, or after a bulk " +
      "edit or a DWG trace to see what it left behind. Read-only: it never changes the model. " +
      "Reports what Revit already flagged — for norm compliance use run_norm_audit or the check_* tools instead.",
    {
      severity: z
        .enum(["Warning", "Error", "DocumentCorruption"])
        .optional()
        .describe(
          "Keep only this severity. Omit for everything; errorCount in the response already " +
            "tells you whether anything worse than a warning is present."
        ),
      maxElementIdsPerGroup: z
        .number()
        .int()
        .min(0)
        .max(500)
        .optional()
        .default(20)
        .describe(
          "Element ids sampled per group (default 20). elementCount always reports the real total, " +
            "and elementIdsTruncated marks a sampled list. 0 returns counts only."
        ),
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
        .max(500)
        .optional()
        .default(50)
        .describe(
          "Max warning groups per call (default 50). totalWarnings and totalGroups still describe the whole model."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_model_warnings", {
            maxElementIdsPerGroup: args.maxElementIdsPerGroup ?? 20,
            ...(args.severity ? { severity: args.severity } : {}),
          });
        });

        // A model in bad shape can carry hundreds of distinct warning kinds; the
        // top groups are the ones worth acting on, and Revit already sorted them.
        const trimmed = paginateRows(response, {
          key: "groups",
          offset: args.offset ?? 0,
          limit: args.limit ?? 50,
          fields: ["all"],
        });

        return {
          content: [{ type: "text" as const, text: JSON.stringify(trimmed) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `get_model_warnings не выполнен: ${
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
