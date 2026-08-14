import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { normalizeCategoryNames } from "../utils/revitCategories.js";

export function registerTagElementsTool(server: McpServer) {
  server.tool(
    "tag_elements",
    "Place real Revit tags (marks) on elements in a view, by category or by element ids. " +
      "Use this for door/window/wall marks instead of drawing text notes — a tag shows the " +
      "element's own Mark parameter and updates with the model. Tags the active view by default.",
    {
      category: z
        .string()
        .optional()
        .describe(
          "Category to tag, e.g. 'OST_Doors', 'OST_Windows', 'OST_Walls'. Plain names ('doors', 'двери') are accepted. Ignored when elementIds is given."
        ),
      elementIds: z
        .array(z.number())
        .optional()
        .describe("Specific element ids to tag. Takes precedence over category."),
      useLeader: z
        .boolean()
        .optional()
        .default(false)
        .describe("Draw a leader line from the tag to the element"),
      tagTypeId: z
        .number()
        .optional()
        .describe(
          "Element id of the tag family type to use. Omit to pick the project's tag for that category."
        ),
      viewId: z
        .number()
        .optional()
        .describe("View to tag in. Omit for the active view."),
    },
    async (args, extra) => {
      const elementIds = args.elementIds ?? [];
      const { categories, unresolved } = normalizeCategoryNames(args.category);

      if (elementIds.length === 0 && categories.length === 0) {
        return {
          content: [
            {
              type: "text",
              text: JSON.stringify({
                Success: false,
                Message:
                  unresolved.length > 0
                    ? `Категория «${unresolved.join(", ")}» не распознана. Укажите OST_Doors / OST_Windows / OST_Walls либо простое имя (двери, окна, стены).`
                    : "Укажите category (например OST_Doors) или elementIds.",
                Response: [],
              }),
            },
          ],
        };
      }

      const params = {
        category: categories[0],
        elementIds,
        useLeader: args.useLeader ?? false,
        tagTypeId: args.tagTypeId !== undefined ? String(args.tagTypeId) : undefined,
        viewId: args.viewId ?? -1,
      };

      const response = await withRevitConnection(async (revitClient) => {
        return await revitClient.sendCommand("tag_elements", params);
      });

      return {
        content: [{ type: "text", text: JSON.stringify(response) }],
      };
    }
  );
}
