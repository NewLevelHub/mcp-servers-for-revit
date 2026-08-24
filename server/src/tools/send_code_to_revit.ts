import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const transactionModeSchema = z
  .enum(["auto", "none", "trial"])
  .default("auto")
  .describe(
    "How the snippet should interact with Revit transactions. 'auto' wraps the snippet in a transaction that commits. 'none' is for code that manages its own transactions. 'trial' (REV-175) runs the snippet in a transaction that is ALWAYS rolled back, regardless of outcome — use it to preview what the code would do (see intentReport in the response) before running it for real with 'auto'."
  );

export function registerSendCodeToRevitTool(server: McpServer) {
  server.tool(
    "send_code_to_revit",
    "Send C# code to Revit for execution. The code will be inserted into a template with access to the Revit Document and parameters. Your code should be written to work within the Execute method of the template. The sandbox (REV-175) blocks filesystem/network/process APIs, caps how many elements a run may create+delete, and stops runaway loops — prefer transactionMode: 'trial' first to see intentReport before committing with 'auto'.",
    {
      code: z
        .string()
        .describe(
          "The C# code to execute in Revit. This code will be inserted into the Execute method of a template with access to Document and parameters."
        ),
      parameters: z
        .array(z.string())
        .optional()
        .describe(
          "Optional execution parameters that will be passed to your code"
        ),
      transactionMode: transactionModeSchema,
      maxChangedElements: z
        .number()
        .int()
        .positive()
        .optional()
        .describe(
          "Sandbox limit (REV-175): max elements the snippet may create+delete before the run is rejected and rolled back. Default 500, clamped to [1, 20000]."
        ),
      timeoutSeconds: z
        .number()
        .int()
        .positive()
        .optional()
        .describe(
          "Sandbox limit (REV-175): wall-clock budget for loops in the snippet before it's stopped. Default 10, clamped to [1, 120]."
        ),
    },
    async (args) => {
      const params = {
        code: args.code,
        parameters: args.parameters || [],
        transactionMode: args.transactionMode,
        maxChangedElements: args.maxChangedElements,
        timeoutSeconds: args.timeoutSeconds,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("send_code_to_revit", params);
        });

        return {
          content: [
            {
              type: "text",
              text: `Code execution successful!\nResult: ${JSON.stringify(
                response,
                null,
                2
              )}`,
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Code execution failed: ${
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
