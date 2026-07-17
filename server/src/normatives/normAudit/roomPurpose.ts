/**
 * Residential room classification for area / height / depth checkers.
 * Depth (п. 4.4.10.22) uses living rooms only — see isLivingRoomForDepth.
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
  "лестнич",
  "лифт",
  "холл",
  "площадк",
  "мәлімет",
  "дәліз",
  // ПОН / public amenity (REV-50)
  "пон",
  "общественн назначен",
  "помещени обществен",
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
  "детск",
  "кабинет",
  "библиотек",
  "столов",
  "игров",
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

const LIVING_SCOPE_ALIASES: readonly string[] = [
  "жилая",
  "жилое",
  "жилые",
  "жилых",
  "living",
  "living room",
  "жилая комната",
  "жилые комнаты",
  "тұрғын",
  "тұрғын бөлме",
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

  if (text.includes("нежил")) return "excluded";
  if (includesAny(text, EXCLUDED_KEYWORDS)) return "excluded";

  if (includesAny(text, KITCHEN_KEYWORDS) && !includesAny(text, ["гостин"])) {
    return "kitchen";
  }
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

/**
 * Living rooms for depth max (СП РК 3.02-101 п. 4.4.10.22):
 * спальня / гостиная / детская / кабинет — not stairs, corridors, PON, kitchen.
 */
export function isLivingRoomForDepth(name?: string, department?: string): boolean {
  const category = classifyResidentialRoom(name, department);
  return category === "living_room" || category === "bedroom";
}

/** Filter values like «жилая» mean semantic living scope, not a name substring. */
export function isLivingScopeAlias(filter?: string): boolean {
  if (!filter?.trim()) return false;
  const normalized = filter.trim().toLowerCase();
  return LIVING_SCOPE_ALIASES.includes(normalized);
}
