import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

/**
 * Page size sent to Revit (REV-42). The plugin's own default is 500, which
 * measured 185 303 B on «Короткий блок» — the second-largest payload in the
 * metrics log. Sent explicitly so the smaller default reaches architects with a
 * server update, without waiting on a plugin release.
 */
const DEFAULT_ELEMENT_LIMIT = 150;

export function registerGetCurrentViewElementsTool(server: McpServer) {
  server.tool(
    "get_current_view_elements",
    `Get elements from the current active view in Revit. You can filter by model categories (like Walls, Floors) or annotation categories (like Dimensions, Text). Use includeHidden to show/hide invisible elements. Results are paginated: by default limit=${DEFAULT_ELEMENT_LIMIT} elements per page; use offset for subsequent pages. Response includes totalCount and hasMore for large models.`,
    {
      modelCategoryList: z
        .array(z.string())
        .optional()
        .describe(
          "List of Revit model category names (e.g., 'OST_Walls', 'OST_Doors', 'OST_Floors')"
        ),
      annotationCategoryList: z
        .array(z.string())
        .optional()
        .describe(
          "List of Revit annotation category names (e.g., 'OST_Dimensions', 'OST_WallTags', 'OST_TextNotes')"
        ),
      includeHidden: z
        .boolean()
        .optional()
        .describe("Whether to include hidden elements in the results"),
      limit: z
        .number()
        .int()
        .positive()
        .max(2000)
        .optional()
        .default(DEFAULT_ELEMENT_LIMIT)
        .describe(
          `Maximum number of elements to return per page. Default is ${DEFAULT_ELEMENT_LIMIT}. ` +
            "Raise it only when you genuinely need more — a full page of 500 measured 185 KB " +
            "on a large model. Narrow with modelCategoryList instead where you can."
        ),
      offset: z
        .number()
        .int()
        .min(0)
        .optional()
        .describe("Number of matching elements to skip before returning results. Default is 0. Use with limit for paginated loading."),
    },
    async (args, extra) => {
      const params: Record<string, unknown> = {
        includeHidden: args.includeHidden ?? false,
      };
      if (args.modelCategoryList?.length) {
        params.modelCategoryList = args.modelCategoryList;
      }
      if (args.annotationCategoryList?.length) {
        params.annotationCategoryList = args.annotationCategoryList;
      }
      if (args.limit !== undefined) {
        params.limit = args.limit;
      }
      if (args.offset !== undefined) {
        params.offset = args.offset;
      }

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(
            "get_current_view_elements",
            params
          );
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `get current view elements failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
