/**
 * Residential room classification for REV-57 (area / height checkers).
 * v1 uses room name + department; purpose parameters from Revit are follow-up.
 */

export type ResidentialRoomCategory =
  | "living_room"
  | "kitchen"
  | "bathroom"
  | "bedroom"
  | "excluded"
  | "unknown";

const EXCLUDED_KEYWORDS: readonly string[] = [
  "коридор",
  "corridor",
  "тамбур",
  "tambour",
  "vestibule",
  "лоджи",
  "балкон",
  "balcony",
  "loggia",
  "терраса",
  "кладов",
  "гардероб",
  "техн",
  "лестниц",
  "лифт",
  "холл",
  "площадк",
  "мәлімет",
  "дәліз",
];

const LIVING_KEYWORDS: readonly string[] = [
  "жилая",
  "жилое",
  "гостин",
  "общая комната",
  "общая",
  "living",
  "отдых",
  "тұрғын бөлме",
  "қонақ",
];

const KITCHEN_KEYWORDS: readonly string[] = [
  "кухня",
  "кухн",
  "kitchen",
  "ас үй",
  "асхана",
];

const BATHROOM_KEYWORDS: readonly string[] = [
  "санузел",
  "сануз",
  "санитарный",
  "санитарн",
  "ванная",
  "ванной",
  "ванную",
  "туалет",
  "душевая",
  "душевой",
  "wc",
  "дәретхана",
  "жуынатын",
  // Do NOT use bare "душ" — matches «воздушная» / «воздушный».
];

const BEDROOM_KEYWORDS: readonly string[] = [
  "спальн",
  "bedroom",
  "жатын",
  "спал",
];

function blob(name?: string, department?: string): string {
  return `${name ?? ""} ${department ?? ""}`.toLowerCase().trim();
}

function includesAny(text: string, keywords: readonly string[]): boolean {
  return keywords.some((keyword) => text.includes(keyword));
}

/** Classify a room for residential area/height norms. */
export function classifyResidentialRoom(
  name?: string,
  department?: string
): ResidentialRoomCategory {
  const text = blob(name, department);
  if (!text) return "unknown";

  if (includesAny(text, EXCLUDED_KEYWORDS)) return "excluded";

  if (includesAny(text, KITCHEN_KEYWORDS)) return "kitchen";
  if (includesAny(text, BATHROOM_KEYWORDS)) return "bathroom";
  if (includesAny(text, BEDROOM_KEYWORDS)) return "bedroom";
  if (includesAny(text, LIVING_KEYWORDS)) return "living_room";

  return "unknown";
}

/** True when the room should be checked for min height (any classified residential room). */
export function isResidentialRoomForHeight(category: ResidentialRoomCategory): boolean {
  return (
    category === "living_room" ||
    category === "kitchen" ||
    category === "bathroom" ||
    category === "bedroom"
  );
}
