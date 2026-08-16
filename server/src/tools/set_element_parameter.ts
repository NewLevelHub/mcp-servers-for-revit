import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerSetElementParameterTool(server: McpServer) {
  server.tool(
    "set_element_parameter",
    "Write ONE parameter on ONE Revit element. Supports string, number, boolean (yes/no), and element id " +
      "values. Runs inside a Revit transaction. For more than one element or more than one parameter use " +
      "set_elements_parameters — it does the whole batch in a single call instead of one round trip each.",
    {
      elementId: z
        .number()
        .int()
        .positive()
        .describe("Revit element id to modify"),
      parameterName: z
        .string()
        .min(1)
        .describe("Parameter name as shown in Revit properties (case-insensitive lookup)"),
      value: z
        .union([z.string(), z.number(), z.boolean()])
        .describe(
          "New parameter value. Use boolean for yes/no parameters, number for numeric/element id parameters, string for text parameters."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("set_element_parameter", {
            elementId: args.elementId,
            parameterName: args.parameterName,
            value: args.value,
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
              text: `set_element_parameter failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
          // Without this a dropped connection or a timeout reaches the model as a
          // successful result: toolOutcome can only normalise thrown errors and
          // JSON bodies, and this branch returns neither.
          isError: true,
        };
      }
    }
  );
}
