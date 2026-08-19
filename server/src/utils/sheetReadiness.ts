/**
 * Pre-issue check for a drawing set: what is still blank or wrong on the sheets
 * (REV-47).
 *
 * Pure logic — the Revit reads live in the tool. Everything here answers one
 * question a ГИП asks before signing off: *is this set actually ready to go out?*
 * Blank штамп lines and duplicate sheet numbers are what gets a set sent back,
 * and neither shows up in a norm audit.
 *
 * Field aliases come from `titleBlock.ts`, so a project whose штамп this tool can
 * check is exactly one whose штамп `fill_title_block` can fill.
 */
import { SHEET_FIELD_ALIASES, naturalCompareSheetNumbers } from "./titleBlock.js";

/** Штамп lines that must be filled before a set goes out, in reporting order. */
export const REQUIRED_SHEET_FIELDS = [
  "drawnBy",
  "checkedBy",
  "chiefEngineer",
  "normControl",
] as const;

export type RequiredSheetField = (typeof REQUIRED_SHEET_FIELDS)[number];

/** Human labels — the model reports to an architect, not to a schema. */
export const SHEET_FIELD_LABELS: Record<string, string> = {
  drawnBy: "Разработал",
  checkedBy: "Проверил",
  chiefEngineer: "ГИП",
  normControl: "Н. контроль",
  issueDate: "Дата выпуска",
  totalSheets: "Листов",
};

export interface SheetParameter {
  name: string;
  displayValue?: string;
}

export interface SheetInput {
  id: number;
  /** Sheet name, as `ai_element_filter` reports it. */
  name: string;
  /** «Номер листа», already resolved by the caller. */
  number: string;
  parameters: SheetParameter[];
}

export type SheetIssueKind =
  | "missing_number"
  | "duplicate_number"
  | "missing_name"
  | "empty_field"
  | "field_absent";

export interface SheetIssue {
  kind: SheetIssueKind;
  /** Штамп field key for `empty_field` / `field_absent`. */
  field?: string;
  /** What to show the architect. */
  detail: string;
}

export interface SheetReport {
  id: number;
  number: string;
  name: string;
  ready: boolean;
  issues: SheetIssue[];
}

export interface ReadinessSummary {
  totalSheets: number;
  readySheets: number;
  sheetsWithIssues: number;
  duplicateNumbers: string[];
  /** How many sheets each штамп field is blank on, worst first. */
  blankFieldCounts: Array<{ field: string; label: string; sheets: number }>;
}

export interface ReadinessReport {
  summary: ReadinessSummary;
  sheets: SheetReport[];
}

function hasParameter(parameters: SheetParameter[], aliases: readonly string[]): boolean {
  return parameters.some((parameter) =>
    aliases.some((alias) => parameter.name.toLowerCase() === alias.toLowerCase())
  );
}

function valueOf(parameters: SheetParameter[], aliases: readonly string[]): string {
  for (const alias of aliases) {
    const match = parameters.find(
      (parameter) => parameter.name.toLowerCase() === alias.toLowerCase()
    );
    if (match) return (match.displayValue ?? "").trim();
  }
  return "";
}

/** Sheet numbers that appear on more than one sheet, in sheet order. */
export function findDuplicateNumbers(sheets: readonly SheetInput[]): string[] {
  const seen = new Map<string, number>();
  for (const sheet of sheets) {
    const number = sheet.number.trim();
    if (!number) continue;
    seen.set(number, (seen.get(number) ?? 0) + 1);
  }
  return [...seen.entries()]
    .filter(([, count]) => count > 1)
    .map(([number]) => number)
    .sort(naturalCompareSheetNumbers);
}

/**
 * Grade every sheet and roll the result up.
 *
 * `fields` names which штамп lines to require; defaults to
 * {@link REQUIRED_SHEET_FIELDS}. A field the project's title block simply does
 * not have is reported as `field_absent`, distinct from a field that exists and
 * is blank — the first needs a different template, the second needs someone to
 * type a name, and telling an architect to "fill in Н.контроль" on a штамп that
 * has no such line wastes their time.
 */
export function buildReadinessReport(
  sheets: readonly SheetInput[],
  fields: readonly string[] = REQUIRED_SHEET_FIELDS
): ReadinessReport {
  const duplicates = new Set(findDuplicateNumbers(sheets));
  const blankCounts = new Map<string, number>();

  const reports: SheetReport[] = sheets.map((sheet) => {
    const issues: SheetIssue[] = [];
    const number = sheet.number.trim();

    if (!number) {
      issues.push({ kind: "missing_number", detail: "У листа не заполнен «Номер листа»." });
    } else if (duplicates.has(number)) {
      issues.push({
        kind: "duplicate_number",
        detail: `Номер «${number}» стоит больше чем на одном листе.`,
      });
    }

    if (!sheet.name.trim()) {
      issues.push({ kind: "missing_name", detail: "У листа не заполнено имя." });
    }

    for (const field of fields) {
      const aliases = SHEET_FIELD_ALIASES[field];
      const label = SHEET_FIELD_LABELS[field] ?? field;
      if (!aliases) continue;

      if (!hasParameter(sheet.parameters, aliases)) {
        issues.push({
          kind: "field_absent",
          field,
          detail: `В штампе нет строки «${label}» — другой шаблон основной надписи.`,
        });
        continue;
      }

      if (!valueOf(sheet.parameters, aliases)) {
        issues.push({ kind: "empty_field", field, detail: `Не заполнено «${label}».` });
        blankCounts.set(field, (blankCounts.get(field) ?? 0) + 1);
      }
    }

    return {
      id: sheet.id,
      number: sheet.number,
      name: sheet.name,
      ready: issues.length === 0,
      issues,
    };
  });

  const blankFieldCounts = [...blankCounts.entries()]
    .map(([field, count]) => ({
      field,
      label: SHEET_FIELD_LABELS[field] ?? field,
      sheets: count,
    }))
    .sort((a, b) => b.sheets - a.sheets || a.field.localeCompare(b.field));

  return {
    summary: {
      totalSheets: reports.length,
      readySheets: reports.filter((sheet) => sheet.ready).length,
      sheetsWithIssues: reports.filter((sheet) => !sheet.ready).length,
      duplicateNumbers: [...duplicates].sort(naturalCompareSheetNumbers),
      blankFieldCounts,
    },
    sheets: reports,
  };
}

/** One line an architect can act on, or a clean bill of health. */
export function summarizeReadiness(summary: ReadinessSummary): string {
  if (summary.totalSheets === 0) return "В проекте нет листов.";
  if (summary.sheetsWithIssues === 0) {
    return `Все ${summary.totalSheets} листов заполнены — к выдаче готовы.`;
  }

  const parts = [
    `Замечания на ${summary.sheetsWithIssues} из ${summary.totalSheets} листов.`,
  ];
  if (summary.duplicateNumbers.length > 0) {
    parts.push(`Повторяются номера: ${summary.duplicateNumbers.join(", ")}.`);
  }
  const worst = summary.blankFieldCounts[0];
  if (worst) {
    parts.push(`Чаще всего пусто «${worst.label}» — на ${worst.sheets} листах.`);
  }
  return parts.join(" ");
}
