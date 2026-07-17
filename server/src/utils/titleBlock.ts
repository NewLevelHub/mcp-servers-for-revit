/**
 * Title block (основная надпись, штамп СПДС / ГОСТ 21.501) fill helpers.
 *
 * Pure logic: field-name aliases for RU/EN/ADSK project templates, sheet
 * auto-numbering plan with unique-collision safety. The fill_title_block tool
 * orchestrates existing Revit commands (ai_element_filter, get_element_parameters,
 * set_element_parameter via batch_execute) — no new families, no C# changes.
 */

/** Well-known штамп fields on the SHEET, with parameter-name aliases in priority order. */
export const SHEET_FIELD_ALIASES: Record<string, readonly string[]> = {
  drawnBy: ["Разработал", "ADSK_Разработал", "Drawn By", "Автор"],
  checkedBy: ["Проверил", "ADSK_Проверил", "Checked By", "Проверено"],
  chiefEngineer: ["ГИП", "ADSK_ГИП", "Утвердил", "Approved By", "Утверждено"],
  normControl: ["Н.контроль", "Н. контроль", "ADSK_Н.контроль", "Нормоконтроль"],
  issueDate: ["Дата выпуска листа", "Sheet Issue Date", "ADSK_Дата", "Дата"],
  totalSheets: ["Листов", "ADSK_Количество листов", "Всего листов"],
} as const;

/** Штамп fields on PROJECT INFORMATION (labels in the frame family read them). */
export const PROJECT_FIELD_ALIASES: Record<string, readonly string[]> = {
  code: ["Номер проекта", "Project Number", "ADSK_Шифр проекта", "Шифр проекта"],
  name: ["Наименование проекта", "Project Name", "ADSK_Наименование проекта"],
  stage: [
    "Статус проекта",
    "Project Status",
    "Стадия",
    "ADSK_Стадия проектирования",
    "ADSK_Стадия",
  ],
  client: ["Имя заказчика", "Client Name", "ADSK_Заказчик", "Заказчик"],
  building: ["Наименование здания", "Building Name", "ADSK_Наименование объекта"],
} as const;

export const SHEET_NUMBER_ALIASES: readonly string[] = ["Номер листа", "Sheet Number"];

export interface AvailableParameter {
  name: string;
  isReadOnly?: boolean;
}

export interface ResolvedParameter {
  /** Actual parameter name in the project template, or undefined if absent. */
  name?: string;
  /** The alias matched a parameter, but it is read-only. */
  readOnlyMatch?: string;
}

/** First alias that names a writable parameter wins; read-only matches reported. */
export function resolveParameterName(
  available: AvailableParameter[],
  aliases: readonly string[]
): ResolvedParameter {
  let readOnlyMatch: string | undefined;
  for (const alias of aliases) {
    const match = available.find(
      (parameter) => parameter.name.toLowerCase() === alias.toLowerCase()
    );
    if (!match) continue;
    if (match.isReadOnly) {
      readOnlyMatch = readOnlyMatch ?? match.name;
      continue;
    }
    return { name: match.name };
  }
  return { readOnlyMatch };
}

/** Natural compare so «АР-10» sorts after «АР-9», not after «АР-1». */
export function naturalCompareSheetNumbers(a: string, b: string): number {
  const tokenize = (value: string): (string | number)[] =>
    (value.match(/\d+|\D+/g) ?? []).map((token) =>
      /^\d+$/.test(token) ? Number(token) : token.toLowerCase()
    );

  const ta = tokenize(a);
  const tb = tokenize(b);
  const length = Math.max(ta.length, tb.length);
  for (let i = 0; i < length; i++) {
    const xa = ta[i];
    const xb = tb[i];
    if (xa === undefined) return -1;
    if (xb === undefined) return 1;
    if (xa === xb) continue;
    if (typeof xa === "number" && typeof xb === "number") return xa - xb;
    return String(xa) < String(xb) ? -1 : 1;
  }
  return 0;
}

export interface SheetForNumbering {
  id: number;
  number: string;
}

export interface AutoNumberOptions {
  startNumber?: number;
  prefix?: string;
  /** Zero-pad width for the numeric part, e.g. 2 → «01». 0 = no padding. */
  padWidth?: number;
  /** "current" (default) keeps natural order of existing numbers; "given" keeps input order. */
  order?: "current" | "given";
}

export interface AutoNumberAssignment {
  id: number;
  from: string;
  to: string;
}

export interface AutoNumberPlan {
  /** Final numbers, in application order. Only sheets whose number changes. */
  assignments: AutoNumberAssignment[];
  /**
   * Temporary numbers applied before the final pass when a target collides
   * with another sheet's current number (Revit requires unique sheet numbers).
   */
  tempAssignments: AutoNumberAssignment[];
  /** Sheet order used, as [id, finalNumber] for every sheet in scope. */
  finalNumbers: Array<{ id: number; number: string }>;
}

export function buildAutoNumberPlan(
  sheets: SheetForNumbering[],
  options: AutoNumberOptions = {}
): AutoNumberPlan {
  const start = options.startNumber ?? 1;
  const prefix = options.prefix ?? "";
  const padWidth = options.padWidth ?? 0;
  const order = options.order ?? "current";

  const ordered =
    order === "given"
      ? [...sheets]
      : [...sheets].sort((a, b) => naturalCompareSheetNumbers(a.number, b.number));

  const finalNumbers = ordered.map((sheet, index) => {
    const numeric = String(start + index);
    const padded = padWidth > 0 ? numeric.padStart(padWidth, "0") : numeric;
    return { id: sheet.id, number: `${prefix}${padded}` };
  });

  const currentById = new Map(sheets.map((sheet) => [sheet.id, sheet.number]));
  const assignments: AutoNumberAssignment[] = finalNumbers
    .filter((entry) => currentById.get(entry.id) !== entry.number)
    .map((entry) => ({
      id: entry.id,
      from: currentById.get(entry.id) ?? "",
      to: entry.number,
    }));

  // A target may equal another sheet's current number (e.g. shifting 2→1, 3→2);
  // route every changed sheet through a unique temp number first.
  const currentNumbers = new Set(sheets.map((sheet) => sheet.number));
  const changedIds = new Set(assignments.map((assignment) => assignment.id));
  const collision = assignments.some(
    (assignment) =>
      currentNumbers.has(assignment.to) &&
      sheets.some(
        (sheet) => sheet.number === assignment.to && !changedIds.has(sheet.id)
      )
  ) ||
    assignments.some((assignment, index) =>
      assignments.some(
        (other, otherIndex) => otherIndex !== index && other.from === assignment.to
      )
    );

  const tempAssignments: AutoNumberAssignment[] = collision
    ? assignments.map((assignment, index) => ({
        id: assignment.id,
        from: assignment.from,
        to: `MCPTMP-${index + 1}`,
      }))
    : [];

  return { assignments, tempAssignments, finalNumbers };
}

export function chunk<T>(items: T[], size: number): T[][] {
  const chunks: T[][] = [];
  for (let i = 0; i < items.length; i += size) {
    chunks.push(items.slice(i, i + size));
  }
  return chunks;
}
