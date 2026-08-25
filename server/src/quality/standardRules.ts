/**
 * Rule engine for `check_model_standard` (REV-179).
 *
 * Grading lives here, deliberately apart from the Revit read
 * (`CheckModelStandardEventHandler.cs`): the C# side reports raw facts — what
 * types exist, how many elements sit without a level, which workset a category
 * really lives in — and this module decides what counts as a violation, from a
 * config that is data, not code, because every organization's standard is its
 * own. That split is also what lets these rules run — and be tested — without
 * Revit at all.
 */

export type Severity = "critical" | "fix" | "optional";

export interface Finding {
  severity: Severity;
  /** Rule family: naming, level, workset, duplicate-type, unused-type, group, view, link. */
  category: string;
  message: string;
  elementIds?: number[];
}

export interface RawModelType {
  category: string;
  familyName: string;
  typeName: string;
  typeId: number;
  instanceCount: number;
}

export interface RawCategoryCount {
  category: string;
  count: number;
  sampleElementIds: number[];
}

export interface RawWorksetCategoryCount {
  category: string;
  worksetName: string;
  count: number;
  sampleElementIds: number[];
}

export interface RawWorksetInfo {
  name: string;
  kind: string;
  elementCount: number;
}

export interface RawGroupInfo {
  name: string;
  kind: string;
  instanceCount: number;
  memberCount: number;
}

export interface RawViewInfo {
  name: string;
  viewType: string;
  scale?: number;
  hasTemplate: boolean;
  templateName?: string;
}

export interface RawLinkInfo {
  name: string;
  status: string;
}

/** Shape of `check_model_standard`'s Revit response — mirrors CheckModelStandardResult. */
export interface RawModelFacts {
  worksharingEnabled: boolean;
  worksets: RawWorksetInfo[];
  types: RawModelType[];
  elementsWithoutLevel: RawCategoryCount[];
  worksetByCategory: RawWorksetCategoryCount[];
  groups: RawGroupInfo[];
  views: RawViewInfo[];
  links: RawLinkInfo[];
}

/**
 * The organization's own standard, as data. Every field is optional — an
 * empty (or missing) config still runs the structural checks that need no
 * naming convention at all, which is what "разумный набор по умолчанию" means
 * here: nothing about names is assumed without the org saying so.
 */
export interface StandardConfig {
  /** Regex source, per category — or `"*"` for every category — tested against type names. */
  typeNamePattern?: Record<string, string>;
  /** Regex source, per category or `"*"`, tested against family names (skipped for system types, which have none). */
  familyNamePattern?: Record<string, string>;
  flagElementsWithoutLevel?: boolean;
  flagWorksetOutliers?: boolean;
  flagDuplicateTypeNames?: boolean;
  flagUnusedTypes?: boolean;
  flagSuspiciousGroups?: boolean;
  /** Off by default — plenty of organizations don't mandate view templates. */
  flagViewsWithoutTemplate?: boolean;
  flagBrokenLinks?: boolean;
  /** A workset holding fewer than this many of a category's elements isn't worth a row. Default 1. */
  worksetOutlierMinCount?: number;
  /** Optional ceiling — groups placed more times than this are flagged "optional". Unset = no ceiling. */
  maxGroupInstances?: number;
}

export const DEFAULT_STANDARD_CONFIG: Required<
  Omit<StandardConfig, "typeNamePattern" | "familyNamePattern" | "maxGroupInstances">
> = {
  flagElementsWithoutLevel: true,
  flagWorksetOutliers: true,
  flagDuplicateTypeNames: true,
  flagUnusedTypes: true,
  flagSuspiciousGroups: true,
  flagViewsWithoutTemplate: false,
  flagBrokenLinks: true,
  worksetOutlierMinCount: 1,
};

function resolveConfig(config: StandardConfig | undefined): StandardConfig {
  return { ...DEFAULT_STANDARD_CONFIG, ...(config ?? {}) };
}

function normalizeName(name: string): string {
  return name.trim().toLowerCase().replace(/[\s_-]+/g, " ");
}

/** First pattern that applies to `category` — the category's own entry, else `"*"`. */
function patternFor(patterns: Record<string, string> | undefined, category: string): RegExp | null {
  if (!patterns) return null;
  const source = patterns[category] ?? patterns["*"];
  if (!source) return null;
  try {
    return new RegExp(source);
  } catch {
    // A broken regex in the config should not crash the whole audit — just skip it.
    return null;
  }
}

function checkNaming(facts: RawModelFacts, config: StandardConfig, findings: Finding[]): void {
  const typePatterns = config.typeNamePattern;
  const familyPatterns = config.familyNamePattern;
  if (!typePatterns && !familyPatterns) return;

  for (const type of facts.types) {
    if (typePatterns) {
      const re = patternFor(typePatterns, type.category);
      if (re && !re.test(type.typeName)) {
        findings.push({
          severity: "fix",
          category: "naming",
          message: `Тип «${type.typeName}» (${type.category}) не соответствует шаблону имён проекта.`,
        });
      }
    }
    if (familyPatterns && type.familyName) {
      const re = patternFor(familyPatterns, type.category);
      if (re && !re.test(type.familyName)) {
        findings.push({
          severity: "fix",
          category: "naming",
          message: `Семейство «${type.familyName}» (${type.category}) не соответствует шаблону имён проекта.`,
        });
      }
    }
  }
}

function checkElementsWithoutLevel(facts: RawModelFacts, config: StandardConfig, findings: Finding[]): void {
  if (!config.flagElementsWithoutLevel) return;
  for (const entry of facts.elementsWithoutLevel) {
    if (entry.count <= 0) continue;
    findings.push({
      severity: "critical",
      category: "level",
      message: `${entry.count} элемент(ов) категории «${entry.category}» без уровня — рискует пропасть из спек и планов по уровню.`,
      elementIds: entry.sampleElementIds,
    });
  }
}

function checkWorksetOutliers(facts: RawModelFacts, config: StandardConfig, findings: Finding[]): void {
  if (!config.flagWorksetOutliers || !facts.worksharingEnabled) return;
  const minCount = config.worksetOutlierMinCount ?? 1;

  const byCategory = new Map<string, RawWorksetCategoryCount[]>();
  for (const row of facts.worksetByCategory) {
    const list = byCategory.get(row.category) ?? [];
    list.push(row);
    byCategory.set(row.category, list);
  }

  for (const [category, rows] of byCategory) {
    if (rows.length < 2) continue; // one workset for this category — nothing to compare against
    const majority = rows.reduce((a, b) => (b.count > a.count ? b : a));
    for (const row of rows) {
      if (row === majority || row.count < minCount) continue;
      findings.push({
        severity: "fix",
        category: "workset",
        message:
          `${row.count} элемент(ов) категории «${category}» лежат в ворксете «${row.worksetName}», ` +
          `а большинство (${majority.count}) — в «${majority.worksetName}».`,
        elementIds: row.sampleElementIds,
      });
    }
  }
}

/** Revit's own conflict-rename marker: "Дверь 900" that collided becomes "Дверь 900(2)", "Дверь 900(3)". */
const REVIT_SUFFIX_RE = /^(.*?)\s*\((\d+)\)$/;

/**
 * Two types sharing a display name are common and mostly harmless — different families in the
 * same category (a DWG block and an unrelated door family can both have a type called "900"),
 * and some categories (stair runs/landings, среди прочих) get one auto-named type PER INSTANCE
 * by Revit itself, never meant as reusable types at all. Neither is what the ticket means by
 * "дубли типов («Дверь 900» в трёх экземплярах)" — measured live, treating every same-name pair
 * as a duplicate produced 62 findings on one real model, almost all of them exactly this noise.
 * The one reliable signal is Revit's own conflict marker: within the SAME family, a type whose
 * name is the plain base name and another ending "(2)"/"(3)" — that pairing only exists because
 * something got loaded twice.
 */
function checkDuplicateTypeNames(facts: RawModelFacts, config: StandardConfig, findings: Finding[]): void {
  if (!config.flagDuplicateTypeNames) return;

  const byGroup = new Map<string, { type: RawModelType; hasSuffix: boolean }[]>();
  for (const type of facts.types) {
    const match = REVIT_SUFFIX_RE.exec(type.typeName.trim());
    const baseName = match ? match[1].trim() : type.typeName.trim();
    const key = `${type.category} ${type.familyName} ${normalizeName(baseName)}`;
    const list = byGroup.get(key) ?? [];
    list.push({ type, hasSuffix: !!match });
    byGroup.set(key, list);
  }

  for (const entries of byGroup.values()) {
    const distinctIds = new Set(entries.map((e) => e.type.typeId));
    if (distinctIds.size < 2) continue;
    if (!entries.some((e) => e.hasSuffix)) continue; // same name, no Revit conflict marker — not our business

    const names = entries.map((e) => `«${e.type.typeName}»`).join(", ");
    findings.push({
      severity: "fix",
      category: "duplicate-type",
      message:
        `${distinctIds.size} типа похожи на случайный дубль в категории «${entries[0].type.category}»: ${names}. ` +
        `Revit добавляет «(2)», «(3)» при конфликте имён — похоже, один и тот же тип загружен более одного раза.`,
    });
  }
}

function checkUnusedTypes(facts: RawModelFacts, config: StandardConfig, findings: Finding[]): void {
  if (!config.flagUnusedTypes) return;
  for (const type of facts.types) {
    if (type.instanceCount > 0) continue;
    findings.push({
      severity: "optional",
      category: "unused-type",
      message: `Тип «${type.typeName}» (${type.category}) загружен, но не размещён ни разу.`,
    });
  }
}

function checkGroups(facts: RawModelFacts, config: StandardConfig, findings: Finding[]): void {
  if (!config.flagSuspiciousGroups) return;
  for (const group of facts.groups) {
    if (group.memberCount === 0) {
      findings.push({
        severity: "fix",
        category: "group",
        message: `Группа «${group.name}» (${group.kind}) не содержит элементов.`,
      });
      continue;
    }
    if (config.maxGroupInstances != null && group.instanceCount > config.maxGroupInstances) {
      findings.push({
        severity: "optional",
        category: "group",
        message: `Группа «${group.name}» размещена ${group.instanceCount} раз — больше порога (${config.maxGroupInstances}).`,
      });
    }
  }
}

function checkViews(facts: RawModelFacts, config: StandardConfig, findings: Finding[]): void {
  if (!config.flagViewsWithoutTemplate) return;
  for (const view of facts.views) {
    if (view.hasTemplate) continue;
    const scaleText = view.scale ? `, масштаб 1:${view.scale}` : "";
    findings.push({
      severity: "optional",
      category: "view",
      message: `Вид «${view.name}» (${view.viewType}) без шаблона вида${scaleText}.`,
    });
  }
}

function checkLinks(facts: RawModelFacts, config: StandardConfig, findings: Finding[]): void {
  if (!config.flagBrokenLinks) return;
  for (const link of facts.links) {
    if (link.status === "Loaded") continue;
    const severity: Severity =
      link.status === "NotFound" || link.status === "Invalid" ? "critical" : "optional";
    findings.push({
      severity,
      category: "link",
      message: `Связь «${link.name}»: ${link.status}.`,
    });
  }
}

const SEVERITY_ORDER: Record<Severity, number> = { critical: 0, fix: 1, optional: 2 };

/** All findings, most severe first — never grades a raw payload it wasn't given. */
export function evaluateStandard(facts: RawModelFacts, config?: StandardConfig): Finding[] {
  const resolved = resolveConfig(config);
  const findings: Finding[] = [];

  checkNaming(facts, resolved, findings);
  checkElementsWithoutLevel(facts, resolved, findings);
  checkWorksetOutliers(facts, resolved, findings);
  checkDuplicateTypeNames(facts, resolved, findings);
  checkUnusedTypes(facts, resolved, findings);
  checkGroups(facts, resolved, findings);
  checkViews(facts, resolved, findings);
  checkLinks(facts, resolved, findings);

  return findings.sort((a, b) => SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity]);
}

export function summarizeFindings(findings: readonly Finding[]): Record<Severity, number> {
  const summary: Record<Severity, number> = { critical: 0, fix: 0, optional: 0 };
  for (const f of findings) summary[f.severity]++;
  return summary;
}
