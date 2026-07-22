import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const MAX_BATCH = 100;

export function registerGetElementsParametersTool(server: McpServer) {
  server.tool(
    "get_elements_parameters",
    "Read parameters for multiple Revit elements in one ExternalEvent (faster than many get_element_parameters calls). Supports parameterNames filter and slim payload.",
    {
      elementIds: z
        .array(z.number().int().positive())
        .min(1)
        .max(MAX_BATCH)
        .describe("Revit element ids (max 100 per call)"),
      parameterNames: z
        .array(z.string().min(1))
        .optional()
        .describe("If set, only these parameter names are returned for each element"),
      slim: z
        .boolean()
        .optional()
        .describe(
          "If true, omit rawValue, unitType, isShared, and builtInParameter per parameter"
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_elements_parameters", {
            elementIds: args.elementIds,
            parameterNames: args.parameterNames,
            slim: args.slim ?? false,
          });
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
              text: `get_elements_parameters failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
