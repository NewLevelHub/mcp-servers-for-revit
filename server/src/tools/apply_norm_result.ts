import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerApplyNormResultTool(server: McpServer) {
  server.tool(
    "apply_norm_result",
    "Write norm-check results into the Revit model — status text, marks, schedules. For paint-only requests («подсвети зелёным») prefer highlight_room_tags instead. Pass elements with the norm source, and choose actions: set_parameter, set_mark, highlight (Override Graphics Projection Lines on Room Tags), create_schedule. ALWAYS run with preview=true first unless the user already confirmed write. Existing parameter values are never overwritten unless overwrite=true.",
    {
      elements: z
        .array(
          z.object({
            elementId: z.number().int().describe("Revit element id of a violating element."),
            note: z
              .string()
              .optional()
              .describe("Per-element finding, e.g. 'глубина 2100 мм < 2400 мм'."),
          })
        )
        .min(1)
        .describe("Violating elements from a check_* tool result."),
      norm: z
        .object({
          document: z.string().describe("Document code, e.g. СП РК 3.02-101"),
          clause: z.string().describe("Clause reference, e.g. п. 5.2.4"),
          quote: z.string().optional().describe("Original normative sentence."),
        })
        .describe("Normative source the violations refer to."),
      actions: z
        .array(z.enum(["set_parameter", "set_mark", "highlight", "create_schedule"]))
        .min(1)
        .describe("Which write actions to perform."),
      parameterName: z
        .string()
        .optional()
        .default("Comments")
        .describe(
          "Target text parameter for set_parameter. Defaults to Comments (Комментарии), which exists on all elements."
        ),
      valueTemplate: z
        .string()
        .optional()
        .describe(
          "Optional value template with {document}, {clause}, {note} placeholders. Default: 'Нарушение {document} {clause} — {note}'."
        ),
      markPrefix: z
        .string()
        .optional()
        .default("НК-")
        .describe("Prefix for set_mark values: НК-1, НК-2, ..."),
      scheduleName: z
        .string()
        .optional()
        .describe("Optional schedule name for create_schedule."),
      preview: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "true (default): dry-run, nothing is written — returns planned changes. false: write to the model."
        ),
      overwrite: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Allow replacing existing non-empty parameter values. Keep false unless the user confirmed."
        ),
      highlightColor: z
        .object({
          r: z.number().int().min(0).max(255),
          g: z.number().int().min(0).max(255),
          b: z.number().int().min(0).max(255),
        })
        .optional()
        .describe(
          "Override Graphics color for highlight (Projection Lines). Default red {255,0,0}. For compliant rooms use green e.g. {r:0,g:180,b:0}."
        ),
    },
    async (args) => {
      const params = {
        elements: args.elements.map((element) => ({
          elementId: element.elementId,
          note: element.note ?? "",
        })),
        norm: args.norm,
        actions: args.actions,
        parameterName: args.parameterName ?? "Comments",
        valueTemplate: args.valueTemplate ?? "",
        markPrefix: args.markPrefix ?? "НК-",
        scheduleName: args.scheduleName ?? "",
        preview: args.preview ?? true,
        overwrite: args.overwrite ?? false,
        highlightColor: args.highlightColor,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("apply_norm_result", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(
                {
                  norm: args.norm,
                  ...(typeof response === "object" && response !== null
                    ? response
                    : { result: response }),
                },
                null,
                2
              ),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `apply_norm_result failed: ${
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
