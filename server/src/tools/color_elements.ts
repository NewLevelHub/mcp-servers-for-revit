import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerColorElementsTool(server: McpServer) {
  server.tool(
      "color_elements",
      "Color elements via View Color Scheme (цветовая схема) for Rooms, or Override Graphics for other categories. NOT Annotate → Filled Region («Цветовая область») — for that use create_filled_regions. Prefer create_filled_regions for norm-violation solid fills that look like the Annotate UI; use this tool for soft multi-color schemes by parameter (as on plans with «Номер» / «Назначение»).",
      {
        categoryName: z
            .string()
            .describe("Revit category, e.g. 'Помещения' or 'Rooms' for room color fill scheme; 'Walls', 'Doors' for override splash"),
        parameterName: z
            .string()
            .describe("Parameter to group/color by, e.g. 'Комментарии', 'Имя', 'Назначение'"),
        useGradient: z
            .boolean()
            .optional()
            .default(false)
            .describe("Whether to use a gradient color scheme instead of random colors"),
        customColors: z
            .array(
                z.object({
                  r: z.number().int().min(0).max(255),
                  g: z.number().int().min(0).max(255),
                  b: z.number().int().min(0).max(255),
                })
            )
            .optional()
            .describe("Optional RGB colors in group order (first group → first color). For violations mark them first in parameter values or use Комментарии='НК-нарушение'."),
      },
      async (args) => {
        const params = args;
        try {
          const response = await withRevitConnection(async (revitClient) => {
            return await revitClient.sendCommand("color_splash", params);
          });

          // Format the response into a more user-friendly output
          if (response.success) {
            const coloredGroups = response.results || [];

            let resultText = `Successfully colored ${response.totalElements} elements across ${response.coloredGroups} groups.\n\n`;
            resultText += "Parameter Value Groups:\n";

            coloredGroups.forEach((group: any) => {
              const rgb = group.color;
              resultText += `- "${group.parameterValue}": ${group.count} elements colored with RGB(${rgb.r}, ${rgb.g}, ${rgb.b})\n`;
            });

            return {
              content: [
                {
                  type: "text",
                  text: resultText,
                },
              ],
            };
          } else {
            return {
              content: [
                {
                  type: "text",
                  text: `Color operation failed: ${response.message}`,
                },
              ],
            };
          }
        } catch (error) {
          return {
            content: [
              {
                type: "text",
                text: `Color operation failed: ${
                    error instanceof Error ? error.message : String(error)
                }`,
              },
            ],
          };
        }
      }
  );
}