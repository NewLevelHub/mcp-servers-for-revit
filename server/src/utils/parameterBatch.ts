/**
 * Shared batching for REV-181's two orchestration tools
 * (fill_parameters_by_rule, check_data_completeness): both need "read these
 * parameters for these elements" as a plain `{elementId, fields}` list before
 * handing it to the pure logic in `../quality/fillRules.ts` /
 * `../quality/dataCompleteness.ts`. get_elements_parameters caps a single
 * ExternalEvent at 100 elements (get_elements_parameters.ts's MAX_BATCH), so
 * an id list bigger than that means more than one round trip on the same
 * connection — same reasoning as set_elements_parameters' MAX_ELEMENTS.
 */

/** Matches get_elements_parameters.ts / set_elements_parameters.ts. */
export const PARAM_BATCH_SIZE = 100;

export function chunk<T>(items: readonly T[], size: number): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < items.length; i += size) {
    out.push(items.slice(i, i + size));
  }
  return out;
}

interface RawParameterInfo {
  name: string;
  displayValue?: string;
  hasValue: boolean;
}

interface RawElementParametersResult {
  success: boolean;
  message: string;
  elementId: number;
  elementName?: string;
  category?: string;
  parameters?: RawParameterInfo[];
}

interface RawBatchResult {
  success: boolean;
  message: string;
  results?: RawElementParametersResult[];
}

export interface ElementFields {
  elementId: number;
  elementName?: string;
  category?: string;
  fields: Record<string, string>;
}

export interface ReadFieldsError {
  elementId: number;
  message: string;
}

export interface ReadFieldsOutcome {
  elements: ElementFields[];
  errors: ReadFieldsError[];
}

/**
 * Reads `parameterNames` for `elementIds` in batches of PARAM_BATCH_SIZE, on
 * the connection already held by a withRevitConnection callback. A parameter
 * that doesn't exist on a given element is simply absent from that element's
 * `parameters` array (GetElementsParametersEventHandler.CollectElementParameters
 * skips unknown names rather than erroring) — that's folded into `fields` as
 * "not present", which `hasValue()`/`checkCompleteness` already treat as missing,
 * so callers don't need to special-case it.
 *
 * get_elements_parameters resolves aliases server-side (e.g. requesting "Comments"
 * on a Russian-language project returns a parameter named "Комментарии") but the
 * returned ElementParameterInfo only carries that resolved name, never the alias
 * the caller asked for — confirmed live (REV-181): requesting overwrite-protection
 * on "Comments" silently failed because `fields["Comments"]` never matched
 * anything, since the read had put the value under `fields["Комментарии"]`
 * instead. Fixed by keying under BOTH: when a batch's returned parameter count
 * matches the requested count (the common case — every requested name resolved),
 * zip positionally so the value lands under the name the caller actually typed;
 * always also key under Revit's own resolved name, so a caller that already
 * passed the exact Russian name keeps working too. A per-element count mismatch
 * (some alias didn't resolve on that one element) falls back to keying by the
 * resolved name only for that element — no worse than before this fix.
 */
export async function readElementFields(
  revitClient: { sendCommand: (command: string, params: unknown) => Promise<unknown> },
  elementIds: readonly number[],
  parameterNames: readonly string[]
): Promise<ReadFieldsOutcome> {
  const elements: ElementFields[] = [];
  const errors: ReadFieldsError[] = [];
  const requested = [...parameterNames];

  for (const batch of chunk(elementIds, PARAM_BATCH_SIZE)) {
    const response = (await revitClient.sendCommand("get_elements_parameters", {
      elementIds: batch,
      parameterNames: requested,
      slim: true,
    })) as RawBatchResult;

    for (const result of response.results ?? []) {
      if (!result.success) {
        errors.push({ elementId: result.elementId, message: result.message });
        continue;
      }

      const params = result.parameters ?? [];
      const positional = params.length === requested.length;

      const fields: Record<string, string> = {};
      params.forEach((param, i) => {
        if (!param.hasValue || param.displayValue === undefined) return;
        fields[param.name] = param.displayValue;
        if (positional) fields[requested[i]] = param.displayValue;
      });

      elements.push({
        elementId: result.elementId,
        elementName: result.elementName,
        category: result.category,
        fields,
      });
    }
  }

  return { elements, errors };
}
