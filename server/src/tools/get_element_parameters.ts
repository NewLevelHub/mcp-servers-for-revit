import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetElementParametersTool(server: McpServer) {
  server.tool(
    "get_element_parameters",
    "Read parameters of a Revit element by element id. Optionally filter by parameterNames and use slim=true for a lighter payload (name, displayValue, storageType, isReadOnly, hasValue only).",
    {
      elementId: z
        .number()
        .int()
        .positive()
        .describe("Revit element id to read parameters from"),
      parameterNames: z
        .array(z.string().min(1))
        .optional()
        .describe(
          "If set, only these parameter names are returned (LookupParameter / case-insensitive match). Prefer this over reading all parameters."
        ),
      slim: z
        .boolean()
        .optional()
        .describe(
          "If true, omit rawValue, unitType, isShared, and builtInParameter to reduce payload size. Default false."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_element_parameters", {
            elementId: args.elementId,
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
              text: `get_element_parameters failed: ${
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
