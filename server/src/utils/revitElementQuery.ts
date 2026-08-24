/**
 * Reading elements and their parameters through the generic Revit commands.
 *
 * Extracted from `fill_title_block.ts` when `check_sheet_readiness` needed the
 * same three steps (REV-47): list a category with `ai_element_filter`, read the
 * parameters with `get_elements_parameters`, then match a parameter by any of its
 * RU/EN/ADSK names. Behaviour is unchanged — the shapes here are the ones Revit
 * actually returns, including the wrapped and bare-array variants.
 */
import { z } from "zod";
import type { RevitClientConnection } from "./SocketClient.js";
import { chunk, SHEET_NUMBER_ALIASES } from "./titleBlock.js";
import type { SheetInput } from "./sheetReadiness.js";

/** `get_elements_parameters` accepts at most this many ids per call. */
export const PARAMETER_BATCH_SIZE = 20;

export interface FilteredElement {
  id: number;
  name: string;
}

export interface ElementParameter {
  name: string;
  displayValue: string;
  isReadOnly: boolean;
}

const elementListSchema = z.object({
  elements: z
    .array(z.object({ id: z.number(), name: z.string().optional().default("") }))
    .optional(),
});

export const elementParametersSchema = z.object({
  success: z.boolean().optional(),
  elementId: z.number().optional(),
  parameters: z
    .array(
      z.object({
        name: z.string(),
        displayValue: z.string().optional().default(""),
        isReadOnly: z.boolean().optional().default(false),
      })
    )
    .optional()
    .default([]),
});

export type ElementParameters = z.infer<typeof elementParametersSchema>;

/**
 * Pull the element list out of an `ai_element_filter` reply.
 *
 * The command answers in three shapes depending on the path taken inside Revit —
 * a bare array, `{ elements: [...] }`, or the list under some other key — so all
 * three are handled rather than guessed at.
 */
export function parseFilteredElements(response: unknown): FilteredElement[] {
  if (Array.isArray(response)) {
    return response
      .filter((item) => item && typeof item === "object")
      .map((item) => ({
        id: Number((item as Record<string, unknown>).id ?? (item as Record<string, unknown>).Id),
        name: String(
          (item as Record<string, unknown>).name ?? (item as Record<string, unknown>).Name ?? ""
        ),
      }))
      .filter((item) => Number.isFinite(item.id));
  }
  if (response && typeof response === "object") {
    const parsed = elementListSchema.safeParse(response);
    if (parsed.success && parsed.data.elements) {
      return parsed.data.elements.map((element) => ({
        id: element.id,
        name: element.name,
      }));
    }
    // Some responses wrap the list under a different key — take the first array of objects with ids.
    for (const value of Object.values(response as Record<string, unknown>)) {
      if (Array.isArray(value) && value.length > 0 && typeof value[0] === "object") {
        return parseFilteredElements(value);
      }
    }
  }
  return [];
}

/** Read parameters for many elements, in batches Revit will accept. */
export async function fetchParametersBatch(
  revitClient: RevitClientConnection,
  elementIds: number[]
): Promise<Map<number, ElementParameters>> {
  const byId = new Map<number, ElementParameters>();
  for (const ids of chunk(elementIds, PARAMETER_BATCH_SIZE)) {
    const response = await revitClient.sendCommand("get_elements_parameters", {
      elementIds: ids,
      slim: true,
    });
    const batch = z
      .object({
        success: z.boolean().optional(),
        results: z.array(elementParametersSchema).optional().default([]),
      })
      .safeParse(response);
    if (!batch.success) continue;
    for (const item of batch.data.results) {
      if (item.elementId != null) {
        byId.set(item.elementId, item);
      }
    }
  }
  return byId;
}

export interface SheetReadResult {
  sheets: SheetInput[];
  /** Sheets whose parameters could not be read — grading them would report "every field blank" on someone's work. */
  unreadable: SheetInput[];
}

/**
 * Every sheet in the project, with its штамп parameters — the same three-step
 * read (`ai_element_filter` → `get_elements_parameters` → number by alias) that
 * `check_sheet_readiness` used inline before REV-173 needed the identical read
 * for `export_sheet_set`. One place, so a future fourth caller does not re-copy it.
 */
export async function fetchAllSheetsWithParameters(
  revitClient: RevitClientConnection
): Promise<SheetReadResult> {
  const sheetsResponse = await revitClient.sendCommand("ai_element_filter", {
    data: {
      filterCategory: "OST_Sheets",
      includeTypes: false,
      includeInstances: true,
    },
  });

  const sheetElements = parseFilteredElements(sheetsResponse);
  if (sheetElements.length === 0) return { sheets: [], unreadable: [] };

  const paramsBySheet = await fetchParametersBatch(
    revitClient,
    sheetElements.map((sheet) => sheet.id)
  );

  const sheets: SheetInput[] = sheetElements.map((sheet) => {
    const parameters = paramsBySheet.get(sheet.id)?.parameters ?? [];
    return {
      id: sheet.id,
      name: sheet.name,
      number: findParamValue(parameters, SHEET_NUMBER_ALIASES),
      parameters,
    };
  });

  const unreadable = sheets.filter(
    (sheet) => (paramsBySheet.get(sheet.id)?.parameters ?? []).length === 0
  );

  return { sheets, unreadable };
}

/**
 * First alias that exists on the element, by display value.
 *
 * Returns `""` both when no alias matched and when the matched parameter is
 * blank — callers that care about the difference should check the parameter list
 * themselves.
 */
export function findParamValue(
  parameters: Array<{ name: string; displayValue?: string }>,
  aliases: readonly string[]
): string {
  for (const alias of aliases) {
    const match = parameters.find(
      (parameter) => parameter.name.toLowerCase() === alias.toLowerCase()
    );
    if (match) return match.displayValue ?? "";
  }
  return "";
}
