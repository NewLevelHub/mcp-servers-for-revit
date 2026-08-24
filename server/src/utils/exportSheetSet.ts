import { SHEET_DISCIPLINE_ALIASES } from "./titleBlock.js";
import { buildReadinessReport, REQUIRED_SHEET_FIELDS, type SheetInput } from "./sheetReadiness.js";

/**
 * Turning "выпустить комплект" into a concrete, named file list (REV-173).
 *
 * Pure logic — which sheets go out, what each one is named, why a sheet was
 * left behind — so the rule that decides a name or a skip is exercised in
 * `exportSheetSet.test.ts`, not read off a folder of PDFs after the fact. The
 * Revit command only draws the files this module already decided on.
 */

/** One sheet's values for the filename template. Empty string, never undefined — a missing field is a blank, not a crash. */
export interface SheetFileNameValues {
  code: string;
  discipline: string;
  number: string;
  name: string;
  revision: string;
}

/** «{Шифр}-{Раздел}-{Лист}_{Имя}_{Ревизия}» — the ticket's own example, as the default. */
export const DEFAULT_FILENAME_TEMPLATE = "{code}-{discipline}-{number}_{name}_{revision}";

/** Characters Windows (and, by extension, every network share an architect prints to) refuses in a filename. */
const FORBIDDEN_FILENAME_CHARS = /[\\/:*?"<>|]/g;

export function sanitizeFileNameSegment(value: string): string {
  return value.replace(FORBIDDEN_FILENAME_CHARS, "").trim();
}

/**
 * Fill a `{placeholder}` template with a sheet's values, sanitised for the
 * filesystem. Placeholders are case-insensitive; an unknown one resolves to
 * empty rather than failing the whole export over one typo'd template.
 *
 * A field the project doesn't have (no «Раздел» line, no revision on this
 * sheet) leaves the placeholder empty — the runs of `-`/`_` that empty
 * placeholders leave behind are collapsed to one separator, and the result is
 * trimmed, so a missing раздел reads as `ГП-01_Фасады` and not
 * `ГП--01_Фасады_`.
 */
export function buildSheetFileName(template: string, values: SheetFileNameValues): string {
  const lowerValues: Record<string, string> = {
    code: values.code,
    discipline: values.discipline,
    number: values.number,
    name: values.name,
    revision: values.revision,
  };

  const filled = template.replace(/\{(\w+)\}/g, (_match, key: string) => {
    const raw = lowerValues[key.toLowerCase()];
    return raw !== undefined ? sanitizeFileNameSegment(raw) : "";
  });

  const collapsed = filled.replace(/[-_]{2,}/g, "-").replace(/^[-_]+|[-_]+$/g, "");
  return collapsed || "лист";
}

/**
 * Раздел for one sheet: an explicit штамп field first, else the letters
 * (Cyrillic or Latin) before the first separator in its own number — «АР-01»
 * reads as «АР» without anyone having filled in a «Раздел» parameter.
 */
export function resolveDiscipline(sheetNumber: string, parameters: SheetInput["parameters"]): string {
  for (const alias of SHEET_DISCIPLINE_ALIASES) {
    const match = parameters.find((parameter) => parameter.name.toLowerCase() === alias.toLowerCase());
    const value = (match?.displayValue ?? "").trim();
    if (value) return value;
  }

  const prefix = sheetNumber.match(/^([A-Za-zА-Яа-яЁё]+)/);
  return prefix ? prefix[1] : "";
}

export interface SheetRevisionInfo {
  sheetId: number;
  /** Every revision shown on this sheet, most recent last — same order `GetAllRevisionIds` reports. */
  revisions: Array<{ sequenceNumber: number; description: string }>;
}

export interface SelectSheetsOptions {
  /** Explicit selection — «по списку». Undefined means "every sheet in the project".*/
  sheetIds?: number[];
  /** «по разделу» — case-insensitive exact match against the resolved discipline. */
  discipline?: string;
  /** «по ревизии» — only sheets carrying a revision with this description. */
  revisionDescription?: string;
  /** Which штамп fields readiness is graded on; defaults to the four signatures, same as check_sheet_readiness. */
  readinessFields?: readonly string[];
  /** Export a sheet even if it failed the readiness check. Off by default — a criterion of this ticket. */
  allowNotReady?: boolean;
  fileNameTemplate?: string;
  projectCode?: string;
}

export interface SelectedSheet {
  sheetId: number;
  number: string;
  name: string;
  discipline: string;
  fileName: string;
}

export interface SkippedSheet {
  sheetId: number;
  number: string;
  name: string;
  reason: "not_ready" | "wrong_discipline" | "not_in_list" | "no_such_revision" | "unreadable" | "sheet_not_found";
  detail: string;
}

export interface SheetSelection {
  selected: SelectedSheet[];
  skipped: SkippedSheet[];
}

/**
 * The whole "какие листы и с какими именами" decision, in one place: filter
 * by list/раздел/ревизия, gate on readiness, then name what's left.
 *
 * Order matters for a useful skip reason — a sheet outside the requested
 * раздел is reported as that, not dragged through a readiness grade it was
 * never a candidate for.
 */
export function selectSheetsForExport(
  sheets: readonly SheetInput[],
  unreadable: readonly SheetInput[],
  revisionsBySheet: ReadonlyMap<number, SheetRevisionInfo>,
  options: SelectSheetsOptions
): SheetSelection {
  const selected: SelectedSheet[] = [];
  const skipped: SkippedSheet[] = [];
  const unreadableIds = new Set(unreadable.map((sheet) => sheet.id));

  for (const sheet of unreadable) {
    skipped.push({
      sheetId: sheet.id,
      number: sheet.number,
      name: sheet.name,
      reason: "unreadable",
      detail: "Не удалось прочитать параметры листа.",
    });
  }

  const requestedIds = options.sheetIds ? new Set(options.sheetIds) : null;
  const wantedDiscipline = options.discipline?.trim().toLowerCase();
  const wantedRevision = options.revisionDescription?.trim().toLowerCase();

  // A requested id that names no real sheet — deleted since, or simply mistyped — would
  // otherwise vanish with no trace: not selected, not skipped, no explanation for the gap
  // between "5 requested" and "4 accounted for".
  if (requestedIds) {
    const knownIds = new Set([...sheets, ...unreadable].map((sheet) => sheet.id));
    for (const id of requestedIds) {
      if (!knownIds.has(id)) {
        skipped.push({
          sheetId: id,
          number: "",
          name: "",
          reason: "sheet_not_found",
          detail: "Такого листа в проекте нет — не найден среди OST_Sheets.",
        });
      }
    }
  }

  const readable = sheets.filter((sheet) => !unreadableIds.has(sheet.id));
  const readiness = buildReadinessReport(readable, options.readinessFields ?? REQUIRED_SHEET_FIELDS);
  const readyById = new Map(readiness.sheets.map((report) => [report.id, report.ready]));

  for (const sheet of readable) {
    if (requestedIds && !requestedIds.has(sheet.id)) {
      skipped.push({
        sheetId: sheet.id,
        number: sheet.number,
        name: sheet.name,
        reason: "not_in_list",
        detail: "Не входит в переданный список sheetIds.",
      });
      continue;
    }

    const discipline = resolveDiscipline(sheet.number, sheet.parameters);

    if (wantedDiscipline && discipline.toLowerCase() !== wantedDiscipline) {
      skipped.push({
        sheetId: sheet.id,
        number: sheet.number,
        name: sheet.name,
        reason: "wrong_discipline",
        detail: `Раздел листа «${discipline || "—"}», запрошен «${options.discipline}».`,
      });
      continue;
    }

    let revisionValue = "";
    if (wantedRevision || options.fileNameTemplate?.toLowerCase().includes("{revision}")) {
      const info = revisionsBySheet.get(sheet.id);
      const revisions = info?.revisions ?? [];

      if (wantedRevision) {
        const match = revisions.find((revision) => revision.description.trim().toLowerCase() === wantedRevision);
        if (!match) {
          skipped.push({
            sheetId: sheet.id,
            number: sheet.number,
            name: sheet.name,
            reason: "no_such_revision",
            detail: `На листе нет ревизии «${options.revisionDescription}».`,
          });
          continue;
        }
        revisionValue = String(match.sequenceNumber);
      } else if (revisions.length > 0) {
        // No revision filter, but the template asks for one: the latest shown on this sheet.
        revisionValue = String(revisions[revisions.length - 1].sequenceNumber);
      }
    }

    if (!options.allowNotReady && readyById.get(sheet.id) === false) {
      skipped.push({
        sheetId: sheet.id,
        number: sheet.number,
        name: sheet.name,
        reason: "not_ready",
        detail: "Не прошёл check_sheet_readiness — передайте allowNotReady, если это осознанно.",
      });
      continue;
    }

    const fileName = buildSheetFileName(options.fileNameTemplate ?? DEFAULT_FILENAME_TEMPLATE, {
      code: options.projectCode ?? "",
      discipline,
      number: sheet.number,
      name: sheet.name,
      revision: revisionValue,
    });

    selected.push({ sheetId: sheet.id, number: sheet.number, name: sheet.name, discipline, fileName });
  }

  return { selected, skipped };
}
