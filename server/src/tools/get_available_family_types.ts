import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { normalizeCategoryNames } from "../utils/revitCategories.js";

export function registerGetAvailableFamilyTypesTool(server: McpServer) {
  server.tool(
    "get_available_family_types",
    "Get available family types in the current Revit project. You can filter by category and family name, and limit the number of returned types.",
    {
      categoryList: z
        .array(z.string())
        .optional()
        .describe(
          "List of Revit category names to filter by (e.g., 'OST_Walls', 'OST_Doors', 'OST_Furniture'). Plain names like 'doors' or 'двери' are also accepted."
        ),
      // Zod drops unknown keys, so a `category` argument used to vanish without a
      // word and the unfiltered whole-project catalog came back instead.
      category: z
        .union([z.string(), z.array(z.string())])
        .optional()
        .describe("Alias for categoryList; accepts one category or a list."),
      familyNameFilter: z
        .string()
        .optional()
        .describe("Filter family types by family name (partial match)"),
      limit: z
        .number()
        .optional()
        .describe("Maximum number of family types to return"),
    },
    async (args, extra) => {
      const requested = [
        ...(args.categoryList ?? []),
        ...(typeof args.category === "string"
          ? [args.category]
          : args.category ?? []),
      ];
      const { categories, unresolved } = normalizeCategoryNames(requested);

      // Asking for categories and matching none would silently return the whole
      // catalog — say so instead of answering a question nobody asked.
      if (requested.length > 0 && categories.length === 0) {
        return {
          content: [
            {
              type: "text",
              text: JSON.stringify({
                Success: false,
                Message:
                  `Не распознаны категории: ${unresolved.join(", ")}. ` +
                  "Укажите имя категории Revit вида OST_Doors / OST_Walls " +
                  "(или простое имя: doors, двери, стены, окна, мебель).",
                Response: [],
              }),
            },
          ],
        };
      }

      const params = {
        categoryList: categories,
        familyNameFilter: args.familyNameFilter || "",
        limit: args.limit || 100,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(
            "get_available_family_types",
            params
          );
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(
                unresolved.length > 0
                  ? { ...response, ignoredCategories: unresolved }
                  : response
              ),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `get available family types failed: ${
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
