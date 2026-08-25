/**
 * Pure logic for `check_data_completeness` (REV-181): given per-element parameter
 * values already read from Revit, says which elements are missing which
 * required fields — by element and by field, never just a bare count, per
 * the ticket ("называет конкретные элементы и поля, а не только число").
 */

import { hasValue } from "./fillRules.js";

export interface IncompleteElement {
  elementId: number;
  /** Required fields this element is missing, in the order `requiredParameters` was given. */
  missingFields: string[];
}

export interface CompletenessReport {
  totalChecked: number;
  completeCount: number;
  incompleteCount: number;
  /** How many elements are missing each field, only for fields that are missing anywhere. */
  byField: Record<string, number>;
  /** One row per element that is missing at least one required field. */
  elements: IncompleteElement[];
}

export function checkCompleteness(
  requiredParameters: readonly string[],
  elements: readonly { elementId: number; fields: Readonly<Record<string, string | number | undefined>> }[]
): CompletenessReport {
  const byField: Record<string, number> = {};
  const incomplete: IncompleteElement[] = [];

  for (const { elementId, fields } of elements) {
    const missingFields = requiredParameters.filter((name) => !hasValue(fields[name]));
    if (missingFields.length === 0) continue;

    for (const name of missingFields) byField[name] = (byField[name] ?? 0) + 1;
    incomplete.push({ elementId, missingFields });
  }

  return {
    totalChecked: elements.length,
    completeCount: elements.length - incomplete.length,
    incompleteCount: incomplete.length,
    byField,
    elements: incomplete,
  };
}
