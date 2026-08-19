import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerOperateElementTool(server: McpServer) {
  server.tool(
    "operate_element",
    "Operate on Revit elements by performing actions such as select, selectionBox, setColor, setTransparency, delete, hide, etc.",
    {
      data: z
        .object({
          elementIds: z
            .array(z
              .number()
              .describe("A valid Revit element ID to operate on")
            )
            .default([])
            .describe("Array of Revit element IDs to perform the specified action on. May be empty for ResetIsolate / ResetOverrides with categoryNames."),
          action: z
            .string()
            .describe("The operation to perform on elements. Valid values: Select, SelectionBox, SetColor, SetTransparency, Delete, Hide, TempHide, Isolate, Unhide, ResetIsolate, ResetOverrides, Highlight. ResetOverrides clears view graphic overrides (after norm SetColor). Use categoryNames when elementIds is empty."),
          categoryNames: z
            .array(z.string())
            .optional()
            .describe("For ResetOverrides: reset all elements of these categories on the active view (e.g. Doors, Windows, Ramps)."),
          transparencyValue: z
            .number()
            .default(50)
            .describe("Transparency value (0-100) for SetTransparency action. Higher values increase transparency."),
          colorValue: z
            .array(z.number())
            .default([255, 0, 0])
            .describe("RGB color values for SetColor action. Default is red [255,0,0].")
        })
        .describe("Parameters for operating on Revit elements with specific actions"),
    },
    async (args) => {
      const params = args;

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(
            "operate_element",
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
              text: `Operate elements failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
