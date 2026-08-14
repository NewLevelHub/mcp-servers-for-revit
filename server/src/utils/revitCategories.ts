/**
 * Revit filters categories by the exact `OST_*` enum name. Anything else —
 * "Doors", "двери", "Doors " — resolves to nothing, and the Revit side then
 * silently drops the filter and returns the entire project catalog (188 KB).
 * The model reads that as "the filter worked, there is just a lot", so it
 * re-asks with another spelling instead of correcting itself.
 *
 * Normalising here turns the architect's vocabulary into the enum, and reports
 * what could not be resolved rather than hiding it.
 */

/** Russian and bare-English names for the categories that actually get asked for. */
const CATEGORY_ALIASES: Record<string, string> = {
  walls: "OST_Walls",
  стены: "OST_Walls",
  стена: "OST_Walls",
  doors: "OST_Doors",
  двери: "OST_Doors",
  дверь: "OST_Doors",
  windows: "OST_Windows",
  окна: "OST_Windows",
  окно: "OST_Windows",
  floors: "OST_Floors",
  перекрытия: "OST_Floors",
  полы: "OST_Floors",
  ceilings: "OST_Ceilings",
  потолки: "OST_Ceilings",
  roofs: "OST_Roofs",
  крыши: "OST_Roofs",
  кровля: "OST_Roofs",
  rooms: "OST_Rooms",
  помещения: "OST_Rooms",
  комнаты: "OST_Rooms",
  furniture: "OST_Furniture",
  мебель: "OST_Furniture",
  columns: "OST_Columns",
  колонны: "OST_Columns",
  structuralcolumns: "OST_StructuralColumns",
  "несущие колонны": "OST_StructuralColumns",
  structuralframing: "OST_StructuralFraming",
  балки: "OST_StructuralFraming",
  stairs: "OST_Stairs",
  лестницы: "OST_Stairs",
  railings: "OST_StairsRailing",
  ограждения: "OST_StairsRailing",
  grids: "OST_Grids",
  оси: "OST_Grids",
  levels: "OST_Levels",
  уровни: "OST_Levels",
  sheets: "OST_Sheets",
  листы: "OST_Sheets",
  views: "OST_Views",
  виды: "OST_Views",
  plumbingfixtures: "OST_PlumbingFixtures",
  сантехника: "OST_PlumbingFixtures",
  lightingfixtures: "OST_LightingFixtures",
  светильники: "OST_LightingFixtures",
  genericmodel: "OST_GenericModel",
  "обобщенные модели": "OST_GenericModel",
  casework: "OST_Casework",
  оборудование: "OST_SpecialityEquipment",
  curtainwall: "OST_Walls",
  "витражные стены": "OST_Walls",
};

export type NormalizedCategories = {
  /** Names safe to send to Revit. */
  categories: string[];
  /** Inputs that could not be mapped — the caller must surface these. */
  unresolved: string[];
};

/**
 * Maps loose category names onto `OST_*`. Already-valid `OST_` names pass
 * through untouched (case-corrected), so this never narrows a working call.
 */
export function normalizeCategoryNames(input: unknown): NormalizedCategories {
  const raw = toStringArray(input);
  const categories: string[] = [];
  const unresolved: string[] = [];

  for (const item of raw) {
    const trimmed = item.trim();
    if (!trimmed) continue;

    if (/^ost_/i.test(trimmed)) {
      // "ost_doors" → "OST_Doors" is not safe to guess casing for beyond the prefix,
      // so only the prefix is corrected; Revit's Enum.TryParse is case-insensitive.
      categories.push(trimmed);
      continue;
    }

    const mapped = CATEGORY_ALIASES[trimmed.toLowerCase()];
    if (mapped) {
      categories.push(mapped);
    } else {
      unresolved.push(trimmed);
    }
  }

  return { categories: [...new Set(categories)], unresolved };
}

function toStringArray(input: unknown): string[] {
  if (typeof input === "string") return [input];
  if (Array.isArray(input)) return input.filter((x): x is string => typeof x === "string");
  return [];
}

/** Category names this build knows how to translate, for error messages. */
export function knownCategoryAliases(): string[] {
  return [...new Set(Object.values(CATEGORY_ALIASES))].sort();
}
