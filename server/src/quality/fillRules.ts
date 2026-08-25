/**
 * Rule parsing for `fill_parameters_by_rule` (REV-181).
 *
 * A rule is a template string with `{ParameterName}` tokens, e.g.
 * `"{Тип} {Толщина}мм"` for "Наименование = Тип + толщина" from the ticket.
 * Resolving a template against one element's already-read parameter values
 * is pure string work — no Revit needed, which is what lets it be unit
 * tested directly. The actual parameter reads/writes are the caller's job
 * (fill_parameters_by_rule.ts), via the existing get_elements_parameters /
 * set_elements_parameters tools.
 */

const TOKEN_RE = /\{([^{}]+)\}/g;

/** Every `{ParameterName}` referenced by a template, in order, de-duplicated. */
export function extractTokens(template: string): string[] {
  const seen = new Set<string>();
  const tokens: string[] = [];
  for (const match of template.matchAll(TOKEN_RE)) {
    const name = match[1].trim();
    if (name && !seen.has(name)) {
      seen.add(name);
      tokens.push(name);
    }
  }
  return tokens;
}

export interface ResolvedTemplate {
  /** The substituted string — only meaningful when missingFields is empty. */
  value: string;
  /** Tokens whose field was missing, blank, or not a string/number — the write is skipped for these. */
  missingFields: string[];
}

/**
 * Substitutes every `{ParameterName}` in `template` from `fields`. A field that is
 * missing, undefined, or an empty/whitespace-only string is NOT silently
 * treated as "" — it is reported in `missingFields`, and the caller is
 * expected to skip writing that element rather than write a value with a
 * silent hole in it ("Стена  " with the thickness missing looks like data,
 * not like an error).
 */
export function resolveTemplate(
  template: string,
  fields: Readonly<Record<string, string | number | undefined | null>>
): ResolvedTemplate {
  const missingFields: string[] = [];

  const value = template.replace(TOKEN_RE, (_whole, rawName: string) => {
    const name = rawName.trim();
    const raw = fields[name];
    const text = raw === undefined || raw === null ? "" : String(raw).trim();
    if (text === "") {
      if (!missingFields.includes(name)) missingFields.push(name);
      return "";
    }
    return text;
  });

  return { value, missingFields };
}

/** True when `value` counts as "already filled in" — whitespace-only does not. */
export function hasValue(value: string | number | undefined | null): boolean {
  if (value === undefined || value === null) return false;
  return String(value).trim() !== "";
}

export type FillSkipReason = "missing-source-field" | "already-has-value";

export interface FillPlanRow {
  elementId: number;
  /** Present only when the row will actually be written. */
  newValue?: string;
  currentValue?: string;
  skip?: FillSkipReason;
  /** Which source fields were missing, when skip === "missing-source-field". */
  missingFields?: string[];
}

/**
 * Decides, per element, whether to write and what — never decides overwrite policy
 * implicitly: a non-empty current value is left alone unless `overwrite` is true.
 */
export function planFill(
  template: string,
  targetParameter: string,
  elements: readonly { elementId: number; fields: Readonly<Record<string, string | number | undefined>> }[],
  overwrite: boolean
): FillPlanRow[] {
  return elements.map(({ elementId, fields }) => {
    const currentValue = fields[targetParameter];
    if (!overwrite && hasValue(currentValue)) {
      return {
        elementId,
        currentValue: String(currentValue),
        skip: "already-has-value",
      };
    }

    const resolved = resolveTemplate(template, fields);
    if (resolved.missingFields.length > 0) {
      return {
        elementId,
        currentValue: hasValue(currentValue) ? String(currentValue) : undefined,
        skip: "missing-source-field",
        missingFields: resolved.missingFields,
      };
    }

    return {
      elementId,
      newValue: resolved.value,
      currentValue: hasValue(currentValue) ? String(currentValue) : undefined,
    };
  });
}
