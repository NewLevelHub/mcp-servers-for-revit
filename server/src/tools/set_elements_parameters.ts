import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

/** Matches get_elements_parameters — one page of work per Revit ExternalEvent. */
const MAX_ELEMENTS = 100;

const parameterValueSchema = z.union([z.string(), z.number(), z.boolean()]);

const editSchema = z.object({
  elementId: z.number().int().positive().describe("Revit element id to modify."),
  parameters: z
    .record(parameterValueSchema)
    .describe(
      "Parameter name → new value. Russian and English names both work («Марка» = Mark)."
    ),
});

export type ParameterEdit = {
  elementId: number;
  parameters: Record<string, unknown>;
};

export type BuildEditsArgs = {
  edits?: { elementId: number; parameters: Record<string, unknown> }[];
  elementIds?: number[];
  parameters?: Record<string, unknown>;
};

/**
 * Folds the two accepted call shapes into the one the Revit command takes, or
 * says why it cannot. Refusing here rather than in Revit keeps a malformed call
 * from costing an ExternalEvent round trip.
 */
export function buildEdits(
  args: BuildEditsArgs
): { edits: ParameterEdit[] } | { error: string } {
  const edits: ParameterEdit[] = args.edits?.length
    ? args.edits.map((edit) => ({
        elementId: edit.elementId,
        parameters: edit.parameters,
      }))
    : (args.elementIds ?? []).map((elementId) => ({
        elementId,
        parameters: args.parameters ?? {},
      }));

  if (edits.length === 0) {
    return {
      error:
        "Нечего записывать: передайте либо edits[{elementId, parameters}], " +
        "либо elementIds[] вместе с parameters{}.",
    };
  }

  const emptyEdit = edits.find(
    (edit) => Object.keys(edit.parameters ?? {}).length === 0
  );
  if (emptyEdit) {
    return {
      error:
        `Для элемента ${emptyEdit.elementId} не указано ни одного параметра. ` +
        "При форме elementIds[] нужен непустой parameters{}.",
    };
  }

  return { edits };
}

function refuse(message: string) {
  return {
    content: [
      {
        type: "text" as const,
        text: JSON.stringify({ success: false, message, updatedCount: 0 }),
      },
    ],
  };
}

/**
 * Batch counterpart of set_element_parameter, shaped after get_elements_parameters.
 * Marking three doors was three calls (six, once a spelling missed) because the
 * singular tool takes one element and one parameter per Revit round trip.
 */
export function registerSetElementsParametersTool(server: McpServer) {
  server.tool(
    "set_elements_parameters",
    "Write parameters on many Revit elements in one call and one transaction — prefer this over " +
      "repeating set_element_parameter. Two forms: `edits` for a different value per element " +
      "(e.g. marks Д-1, Д-2, Д-3), or `elementIds` + `parameters` for the same values on all of them. " +
      "Russian and English parameter names both resolve («Марка» = Mark). A failed write does not " +
      "discard the others: the reply reports every write with its own reason, and the call is an " +
      "error only when nothing was written at all.",
    {
      edits: z
        .array(editSchema)
        .max(MAX_ELEMENTS)
        .optional()
        .describe(
          `Per-element writes, max ${MAX_ELEMENTS} elements. Use when values differ per element.`
        ),
      elementIds: z
        .array(z.number().int().positive())
        .max(MAX_ELEMENTS)
        .optional()
        .describe(
          `Shorthand: apply the same \`parameters\` to each of these ids (max ${MAX_ELEMENTS}). Ignored when edits is given.`
        ),
      parameters: z
        .record(parameterValueSchema)
        .optional()
        .describe("Values for the elementIds shorthand. Ignored when edits is given."),
    },
    async (args) => {
      const built = buildEdits(args);
      if ("error" in built) {
        return refuse(built.error);
      }

      const response = await withRevitConnection(async (revitClient) => {
        return await revitClient.sendCommand("set_elements_parameters", {
          edits: built.edits,
        });
      });

      return {
        content: [{ type: "text" as const, text: JSON.stringify(response) }],
      };
    }
  );
}
